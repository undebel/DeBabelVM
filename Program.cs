using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace BabelVMRestore
{
    class Program
    {
        static ModuleDefMD asm;
        static string asmpath;
        static bool verbose = false;
        static void Main(string[] args)
        {
            // Thanks to xeno's old deob and cawk for helping with that one :)

            string oldEnv = Environment.CurrentDirectory;

            Console.Title = "Babel 8.5.0.0 VM Restorer - LaPanthere @ rtn-team.cc";
            Console.WriteLine(@"______    ______       _          _ _   ____  ___");
            Console.WriteLine(@"|  _  \   | ___ \     | |        | | | | |  \/  |");
            Console.WriteLine(@"| | | |___| |_/ / __ _| |__   ___| | | | | .  . |");
            Console.WriteLine(@"| | | / _ \ ___ \/ _` | '_ \ / _ \ | | | | |\/| |");
            Console.WriteLine(@"| |/ /  __/ |_/ / (_| | |_) |  __/ \ \_/ / |  | |");
            Console.WriteLine(@"|___/ \___\____/ \__,_|_.__/ \___|_|\___/\_|  |_/");
            Console.WriteLine("                                 V1.0 - LaPanthere");


            
            try
            {
                asm = ModuleDefMD.Load(args[0]);
                Console.WriteLine("[!]Loading assembly " + asm.FullName);
                asmpath = args[0];
                if (args.Length > 1)
                    verbose = args[1] == "-v";
            }
            catch (Exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[!] Error: Cannot load the file. Make sure it's a valid .NET file!");
                Console.WriteLine("[!] Verbose mode can be activated with -v");
                Console.ForegroundColor = ConsoleColor.White;
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[!] Trying to Restore Methods from VM - for best results move VM Restore to target folder!");
            Console.ForegroundColor = ConsoleColor.White;

            // fix for dll's needed
            Environment.CurrentDirectory = System.IO.Path.GetDirectoryName(asmpath);

            int restored = RestoreDynamicMethods(asm);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[!] Restored {0} methods from VM", restored);
            Console.ForegroundColor = ConsoleColor.White;
            //save module
            string text2 = Path.GetDirectoryName(args[0]);

            if (!text2.EndsWith("\\"))
            {
                text2 += "\\";
            }
            string path = text2 + Path.GetFileNameWithoutExtension(args[0]) + "_patched" +
                          Path.GetExtension(args[0]);

            var opts = new ModuleWriterOptions(asm);
            opts.MetaDataOptions.Flags = MetaDataFlags.PreserveAll;
            opts.Logger = DummyLogger.NoThrowInstance;


            asm.Write(path, opts);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[!] Assembly saved");
            Console.ForegroundColor = ConsoleColor.White;
            Console.ReadKey();
        }
        


        static int RestoreDynamicMethods(ModuleDefMD module)
        {
            List<TypeDef> toDelete = new List<TypeDef>();
            List<EncryptedInfo> InvokeCallerInfo = new List<EncryptedInfo>();
            MethodDef InvokeMethod = null;
            int changes = 0;

            #region Find Invoke Method
            foreach (TypeDef type in module.Types)
            {
                if (type.BaseType == null)
                    continue;
                if (!type.HasInterfaces)
                    continue;

                if (!type.Interfaces[0].Interface.FullName.Contains("IDisposable"))
                    continue;

                foreach (MethodDef md in type.Methods)
                {
                    if (!md.HasBody)
                        continue;
                    if (!md.IsPrivate)
                        continue;
                    if (md.IsStatic)
                        continue;
                    if (md.Parameters.Count < 2)
                        continue;

                    if (md.Parameters[1].Type.FullName != "System.Int32")
                        continue;

                    if (md.Body.ExceptionHandlers.Count != 1)
                        continue;

                    bool skipMethod = false;
                    for (int i = 0; i < md.Body.Instructions.Count; i++)
                    {
                        Instruction inst = md.Body.Instructions[i];

                        if (inst.OpCode == dnlib.DotNet.Emit.OpCodes.Ldstr)
                        {
                            if (((string)inst.Operand) != "Error dynamic method {0}: {1}")
                            {
                                skipMethod = true;
                                break;
                            }
                        }
                    }
                    if (skipMethod)
                        continue;

                    InvokeMethod = md;
                    if (verbose)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(" [!] VM method Found - {0} (RVA: {1}, MDToken: 0x{2:X})", InvokeMethod.FullName, InvokeMethod.RVA, InvokeMethod.MDToken.ToInt32());
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                }
            }
            #endregion

            #region Find Dynamic Method Callers
            foreach (TypeDef type in module.Types)
            {
                foreach (MethodDef md in type.Methods)
                {
                    if (!md.HasBody)
                        continue;

                    EncryptedInfo info = new EncryptedInfo();
                    bool found = false;
                    for (int i = 0; i < md.Body.Instructions.Count; i++)
                    {
                        Instruction inst = md.Body.Instructions[i];

                        if (inst.Operand is MethodDef)
                        {
                            MethodDef mDef = inst.Operand as MethodDef;

                            if (mDef.Parameters.Count != 3)
                                continue;
                            if (mDef.Parameters[0].Type.FullName != "System.Int32")
                                continue;
                            if (mDef.Parameters[1].Type.FullName != "System.Object")
                                continue;
                            if (mDef.Parameters[2].Type.FullName != "System.Object[]")
                                continue;

                            if (!mDef.IsStatic)
                                continue;
                            if (!mDef.IsPublic)
                                continue;
                            if (mDef.ReturnType.FullName != "System.Object")
                                continue;

                            info.Method = md;
                            found = true;
                            string test = "";
                        }
                    }
                    if (found)
                    {
                        for (int i = 0; i < md.Body.Instructions.Count; i++)
                        {
                            Instruction inst = md.Body.Instructions[i];
                            if (inst.OpCode == dnlib.DotNet.Emit.OpCodes.Ldc_I4)
                            {
                                if (info.Key != 0)
                                    continue;
                                info.Key = inst.GetLdcI4Value();
                                InvokeCallerInfo.Add(info);
                                if (verbose)
                                {
                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                    Console.WriteLine(" [!] Encrypted method Found - {0} (RVA: {1}, MDToken: 0x{2:X})", md.FullName, md.RVA, md.MDToken.ToInt32());
                                    Console.ForegroundColor = ConsoleColor.White;
                                }
                            }
                        }
                    }
                }
            }
            #endregion

            #region Invoke Members
            if (InvokeMethod == null)
                return changes;
            Assembly assembly = Assembly.LoadFile(Program.asmpath);
            MethodBase mb = assembly.ManifestModule.ResolveMethod(InvokeMethod.MDToken.ToInt32());
            ConstructorInfo c = mb.DeclaringType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            object a = c.Invoke(new object[] { });
            foreach (EncryptedInfo info in InvokeCallerInfo)
            {
                try
                {
                    object dr = mb.Invoke(a, new object[] { info.Key });
                    Type drType = dr.GetType();
                    // \uE000 is the real char there
                    info.ResolvedDynamicMethod = Helpers.GetInstanceField(drType, dr, "") as System.Reflection.Emit.DynamicMethod;
                    SuperDynamicReader mbr = new SuperDynamicReader(module, info.ResolvedDynamicMethod);
                    mbr.Read();

                    info.ResolvedMethod = mbr.GetMethod();
                    string eafaefea = "";

                    info.Method.Body = info.ResolvedMethod.Body;
                    changes++;

                    if (verbose)
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine(" [!] Encrypted Method Restored - {0} (RVA: {1}, MDToken: 0x{2:X})", info.Method.FullName, info.Method.RVA, info.Method.MDToken.ToInt32());
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                }
                catch (Exception ex)
                {
                    if (verbose)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine(" [!] Failed Restoration 0x{1:X} : {0}", ex, info.Method.MDToken.ToInt32());
                        Console.ForegroundColor = ConsoleColor.White;
                    }

                }
            }

            #endregion
            return changes;
        }

       
    }

}
