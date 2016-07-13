
using dnlib.DotNet;
using System.Reflection.Emit;
namespace BabelVMRestore
{
    public class EncryptedInfo
    {
        public MethodDef Method;
        public int Key;
        public DynamicMethod ResolvedDynamicMethod;
        public MethodDef ResolvedMethod;
    }
}
