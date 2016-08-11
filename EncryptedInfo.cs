
using System.Reflection.Emit;
using dnlib.DotNet;

namespace BabelVMRestore
{
    internal class EncryptedInfo
    {
        public MethodDef Method;
        public int Key;
        public DynamicMethod ResolvedDynamicMethod;
        public MethodDef ResolvedMethod;
    }
}
