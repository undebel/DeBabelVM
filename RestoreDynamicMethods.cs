using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using dnlib.DotNet;
using OpCodes = dnlib.DotNet.Emit.OpCodes;

namespace BabelVMRestore {
    class RestoreDynamicMethods {
        readonly ModuleDefMD _module;
        readonly string _asmPath;
        readonly bool _verbose;

        internal RestoreDynamicMethods(ModuleDefMD module, string asmPath, bool verbose) {
            _module = module;
            _asmPath = asmPath;
            _verbose = verbose;
        }

        internal int Proccess() {
            //Unknown at moment
            //var toDelete = new List<TypeDef>();
            var invokeMethod = FindInvokeMethod();
            if (invokeMethod == null)
                return 0;
            var invokeCallerInfo = FindDynamicMethodCallers();
            return InvokeMembers(invokeMethod, invokeCallerInfo);
        }

        MethodDef FindInvokeMethod() {
            foreach (var type in _module.GetTypes()) {
                if (type.BaseType == null)
                    continue;
                if (!type.HasInterfaces)
                    continue;

                if (!type.Interfaces[0].Interface.FullName.Contains("IDisposable"))
                    continue;

                foreach (var method in type.Methods) {
                    if (!method.HasBody)
                        continue;
                    if (!method.IsPrivate)
                        continue;
                    if (method.IsStatic)
                        continue;
                    if (method.Parameters.Count < 2)
                        continue;

                    if (method.Parameters[1].Type.FullName != "System.Int32")
                        continue;

                    if (method.Body.ExceptionHandlers.Count != 1)
                        continue;

                    var skipMethod =
                        method.Body.Instructions.Where(inst => inst.OpCode == OpCodes.Ldstr)
                            .Any(inst => (string)inst.Operand != "Error dynamic method {0}: {1}");
                    if (skipMethod)
                        continue;

                    if (!_verbose) return method;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $" [!] VM method Found - {method.FullName} (RVA: {method.RVA}, MDToken: 0x{method.MDToken.ToInt32():X})");
                    Console.ForegroundColor = ConsoleColor.White;
                    return method;
                }
            }
            return null;
        }

        IEnumerable<EncryptedInfo> FindDynamicMethodCallers() {
            var invokeCallerInfo = new List<EncryptedInfo>();
            foreach (var type in _module.Types)
                foreach (var method in type.Methods) {
                    if (!method.HasBody)
                        continue;

                    var info = new EncryptedInfo();
                    var found = false;
                    foreach (var inst in method.Body.Instructions) {
                        var methodDef = inst.Operand as MethodDef;
                        if (methodDef?.Parameters.Count != 3)
                            continue;
                        if (methodDef.Parameters[0].Type.FullName != "System.Int32")
                            continue;
                        if (methodDef.Parameters[1].Type.FullName != "System.Object")
                            continue;
                        if (methodDef.Parameters[2].Type.FullName != "System.Object[]")
                            continue;

                        if (!methodDef.IsStatic)
                            continue;
                        if (!methodDef.IsPublic)
                            continue;
                        if (methodDef.ReturnType.FullName != "System.Object")
                            continue;

                        info.Method = method;
                        found = true;
                    }
                    if (!found) continue;
                    foreach (var inst in method.Body.Instructions) {
                        if (inst.OpCode != OpCodes.Ldc_I4) continue;
                        if (info.Key != 0)
                            continue;
                        info.Key = inst.GetLdcI4Value();
                        invokeCallerInfo.Add(info);
                        if (!_verbose) continue;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(
                            $" [!] Encrypted method Found - {method.FullName} (RVA: {method.RVA}, MDToken: 0x{method.MDToken.ToInt32():X})");
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                }
            return invokeCallerInfo;
        }

        int InvokeMembers(IMDTokenProvider invokeMethod, IEnumerable<EncryptedInfo> invokeCallerInfo) {
            var changes = 0;
            if (invokeMethod == null)
                return changes;
            var assembly = Assembly.LoadFile(_asmPath);
            var mb = assembly.ManifestModule.ResolveMethod(invokeMethod.MDToken.ToInt32());
            if (mb.DeclaringType == null) return changes;
            var c = mb.DeclaringType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                Type.EmptyTypes, null);
            var a = c.Invoke(new object[] { });
            foreach (var info in invokeCallerInfo) {
                try {
                    var dr = mb.Invoke(a, new object[] { info.Key });
                    var drType = dr.GetType();
                    // \uE000 is the real char there
                    info.ResolvedDynamicMethod = Helpers.GetInstanceField(drType, dr, "") as DynamicMethod;
                    var mbr = new SuperDynamicReader(_module, info.ResolvedDynamicMethod);
                    mbr.Read();

                    info.ResolvedMethod = mbr.GetMethod();

                    info.Method.Body = info.ResolvedMethod.Body;
                    changes++;

                    if (!_verbose) continue;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(
                        $" [!] Encrypted Method Restored - {info.Method.FullName} (RVA: {info.Method.RVA}, MDToken: 0x{info.Method.MDToken.ToInt32():X})");
                    Console.ForegroundColor = ConsoleColor.White;
                }
                catch (Exception ex) {
                    if (!_verbose) continue;
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(" [!] Failed Restoration 0x{1:X} : {0}", ex, info.Method.MDToken.ToInt32());
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }
            return changes;
        }
    }
}
