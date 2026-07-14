using System.Runtime.CompilerServices;

namespace Exerussus.Payloads
{
    public readonly struct Payload : System.IEquatable<Payload>
    {
        internal readonly int Id;                            // уникальный для живого контейнера

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Payload(int id) => Id = id;

        // Пустой sentinel. Id == 0 (index:0, gen:0) никогда не выдаётся Create(),
        // поэтому None и default(Payload) эквивалентны и всегда невалидны/пусты.
        public static readonly Payload None = new(0);

        // ---- lifecycle ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Payload Create() => new(PayloadStore.Create());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => PayloadStore.Dispose(Id);

        // ---- key ----

        // Считай один раз: static readonly long MyKey = Payload.Uid("my-key");
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Uid(string key) => PayloadStore.Hash(key);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long UidFrom(string key) => PayloadStore.Hash(key);

        // ---- data (быстрый путь — по long uid) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(long uid, long value) => PayloadStore.Set(Id, uid, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(long uid, out long value) => PayloadStore.TryGet(Id, uid, out value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Get(long uid, long fallback = 0) => PayloadStore.Get(Id, uid, fallback);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(long uid) => PayloadStore.Has(Id, uid);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid() => PayloadStore.IsAlive(Id);

        // ---- удобные строковые перегрузки (хешат на каждый вызов — для горячих ключей precompute Uid) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(string key, long value) => PayloadStore.Set(Id, PayloadStore.Hash(key), value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(string key, out long value) => PayloadStore.TryGet(Id, PayloadStore.Hash(key), out value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty() => PayloadStore.IsEmpty(Id);

        // ---- equality (по Id; None == default(Payload)) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Payload other) => Id == other.Id;

        public override bool Equals(object obj) => obj is Payload other && Id == other.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Payload a, Payload b) => a.Id == b.Id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Payload a, Payload b) => a.Id != b.Id;

        // ---- debug ----

        public override string ToString()
        {
#if UNITY_EDITOR
            return PayloadStore.Describe(Id);
#else
            return $"Payload(0x{Id:X8})";
#endif
        }

#if UNITY_EDITOR
        // фича один раз регистрирует, как печатать значение своего ключа
        public static void RegisterDebugFormatter(long uid, System.Func<long, string> formatter)
            => PayloadStore.RegisterFormatter(uid, formatter);
#endif
    }
}