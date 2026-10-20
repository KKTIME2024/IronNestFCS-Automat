// IL2CPP 的 Il2Cppmscorlib 遮蔽了正常 corelib，导致编译器找不到可空引用类型标注
// 所需的 NullableAttribute / NullableContextAttribute。这里手动补齐，编译器会优先使用它们。
// 这些类型仅供编译器消费，运行时无副作用。
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) => NullableFlags = new[] { flag };
        public NullableAttribute(byte[] flags) => NullableFlags = flags;
    }

    [AttributeUsage(
        AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct |
        AttributeTargets.Method | AttributeTargets.Interface | AttributeTargets.Delegate,
        AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}
