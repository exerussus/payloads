using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
#if UNITY_EDITOR
using System.Text;
#endif

namespace Exerussus.Payloads
{
    // Вся «грязь» живёт здесь. Однопоточно (main thread), без локов.
    internal static class PayloadStore
    {
        // Id (int) = [gen:16][index:16]. 65536 одновременно живых контейнеров — с запасом.
        private const int IndexBits = 16;
        private const int IndexMask = (1 << IndexBits) - 1; // 0xFFFF
        private const int GenMask   = (1 << IndexBits) - 1; // верхние 16 бит
        private const int InitialCapacity = 64;

        private sealed class Container
        {
            public int Generation = 1;                       // 0 зарезервирован под invalid/default
            public readonly Dictionary<long, long> Map = new();
        }

        private static Container[] _slots = new Container[InitialCapacity];
        private static int[]       _free  = new int[InitialCapacity]; // стек свободных индексов (LIFO)
        private static int         _freeTop;
        private static int         _next;                    // highwater распределённых индексов

        // Domain reload off → статика переживает play-сессии. Сбрасываем на старте рантайма.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ResetOnPlay()
        {
            _slots   = new Container[InitialCapacity];
            _free    = new int[InitialCapacity];
            _freeTop = 0;
            _next    = 0;
        }

        // ---- lifecycle ----

        public static int Create()
        {
            int index;
            Container c;

            if (_freeTop > 0)
            {
                index = _free[--_freeTop];
                c     = _slots[index];           // generation уже сдвинут при диспоузе → текущий валидный
            }
            else
            {
                index = _next;
                if (index > IndexMask)
                    throw new InvalidOperationException($"Payload slot overflow (> {IndexMask + 1} alive).");

                EnsureSlot(index);
                c = _slots[index];
                if (c == null)
                {
                    c = new Container();
                    _slots[index] = c;
                }
                _next++;
            }

            return Pack(index, c.Generation);
        }

        public static void Dispose(int id)
        {
            if (id == 0) return;                             // None / default(Payload) → no-op

            int index = (int)((uint)id & IndexMask);
            if ((uint)index >= (uint)_next) return;          // мусорный/чужой id

            var c = _slots[index];
            if (c == null) return;

            int gen = (int)(((uint)id >> IndexBits) & GenMask);
            if (c.Generation != gen) return;                 // протухший хэндл / double-dispose / default → no-op

            c.Map.Clear();
            c.Generation = NextGen(c.Generation);            // инвалидируем все старые копии этого id

            if (_freeTop >= _free.Length) Array.Resize(ref _free, _free.Length << 1);
            _free[_freeTop++] = index;                       // контейнер не уничтожаем — пулим
        }

        // ---- data ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set(int id, long uid, long value)
        {
#if UNITY_EDITOR
            Debug.Assert(id != 0, "[Payload] Set вызван на Payload.None — запись игнорируется.");
#endif
            var c = Resolve(id);
            if (c != null) c.Map[uid] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGet(int id, long uid, out long value)
        {
            var c = Resolve(id);
            if (c != null) return c.Map.TryGetValue(uid, out value);
            value = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Get(int id, long uid, long fallback)
        {
            var c = Resolve(id);
            return c != null && c.Map.TryGetValue(uid, out var v) ? v : fallback;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Has(int id, long uid)
        {
            var c = Resolve(id);
            return c != null && c.Map.ContainsKey(uid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAlive(int id) => Resolve(id) != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEmpty(int id)
        {
            var c = Resolve(id);
            return c == null || c.Map.Count == 0;   // мёртвый/протухший контейнер тоже «пуст»
        }

        // ---- internals ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Container Resolve(int id)
        {
            if (id == 0) return null;                        // None / default(Payload) → ранний заворот

            int index = (int)((uint)id & IndexMask);
            if ((uint)index >= (uint)_next) return null;

            var c = _slots[index];
            if (c == null) return null;

            int gen = (int)(((uint)id >> IndexBits) & GenMask);
            return c.Generation == gen ? c : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Pack(int index, int gen)
            => (int)(((uint)(gen & GenMask) << IndexBits) | (uint)(index & IndexMask));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextGen(int g)
        {
            g = (g + 1) & GenMask;
            return g == 0 ? 1 : g;                            // пропускаем 0
        }

        private static void EnsureSlot(int index)
        {
            if (index < _slots.Length) return;
            int n = _slots.Length;
            while (n <= index) n <<= 1;
            Array.Resize(ref _slots, n);
            Array.Resize(ref _free, n);                       // свободных не больше, чем всего слотов
        }

        // FNV-1a 64. Стабильный, без хранения строки, без детекта коллизий (на 64 битах пренебрежимо).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Hash(string key)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < key.Length; i++)
                {
                    h ^= key[i];
                    h *= 1099511628211UL;
                }
                long uid = (long)h;
#if UNITY_EDITOR
                RegisterKeyName(uid, key);   // в билде строки нет → вызов вырезается препроцессором
#endif
                return uid;
            }
        }

        // =====================================================================
        //  EDITOR-ONLY DEBUG. Вырезается целиком вне UNITY_EDITOR.
        // =====================================================================
#if UNITY_EDITOR
        // uid → исходная строка. Наполняется бесплатно: все ключи идут через Hash().
        private static readonly Dictionary<long, string>             KeyNames   = new();
        // uid → как печатать значение этого ключа. Регистрируют сами фичи.
        private static readonly Dictionary<long, Func<long, string>> Formatters = new();

        private static void RegisterKeyName(long uid, string key)
        {
            if (KeyNames.TryGetValue(uid, out var existing))
            {
                if (!string.Equals(existing, key, StringComparison.Ordinal))
                    Debug.LogWarning($"[Payload] uid collision: \"{existing}\" и \"{key}\" → 0x{uid:X16}");
                return;
            }
            KeyNames[uid] = key;
        }

        public static void RegisterFormatter(long uid, Func<long, string> f) => Formatters[uid] = f;

        private static string DescribeKey(long uid)
            => KeyNames.TryGetValue(uid, out var n) ? n : $"0x{uid:X16}";

        private static string DescribeValue(long uid, long value)
            => Formatters.TryGetValue(uid, out var f) ? f(value) : value.ToString();

        public static string Describe(int id)
        {
            if (id == 0) return "Payload.None";

            int index = (int)((uint)id & IndexMask);
            int gen   = (int)(((uint)id >> IndexBits) & GenMask);

            if ((uint)index >= (uint)_next || _slots[index] == null)
                return $"Payload(idx:{index} gen:{gen}) <unallocated>";

            var c = _slots[index];
            if (c.Generation != gen)
                return $"Payload(idx:{index} gen:{gen}) <stale, container gen:{c.Generation}>";

            if (c.Map.Count == 0)
                return $"Payload(idx:{index}) {{ }}";

            var sb = new StringBuilder(64).Append("Payload(idx:").Append(index).Append(") { ");
            bool first = true;
            foreach (var kv in c.Map)   // enumerator у Dictionary — struct, без heap-аллокаций
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(DescribeKey(kv.Key)).Append(" = ").Append(DescribeValue(kv.Key, kv.Value));
            }
            return sb.Append(" }").ToString();
        }
#endif
    }
}