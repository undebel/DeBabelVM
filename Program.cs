using System;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace BabelVMRestore
{
    class Program
    {
        static ModuleDefMD _moduleDef;
        static string _asmPath;
        static bool _verbose;

        static void Main(string[] args) {
            // Thanks to xeno's old deob and cawk for helping with that one :)

            Console.Title = "Babel 8.5.0.0 VM Restorer - LaPanthere @ rtn-team.cc";
            Console.WriteLine(@"______    ______       _          _ _   ____  ___");
            Console.WriteLine(@"|  _  \   | ___ \     | |        | | | | |  \/  |");
            Console.WriteLine(@"| | | |___| |_/ / __ _| |__   ___| | | | | .  . |");
            Console.WriteLine(@"| | | / _ \ ___ \/ _` | '_ \ / _ \ | | | | |\/| |");
            Console.WriteLine(@"| |/ /  __/ |_/ / (_| | |_) |  __/ \ \_/ / |  | |");
            Console.WriteLine(@"|___/ \___\____/ \__,_|_.__/ \___|_|\___/\_|  |_/");
            Console.WriteLine("                                V1.0 - LaPanthere");
            Console.WriteLine();



            try {
                _moduleDef = ModuleDefMD.Load(args[0]);
                Console.WriteLine("[!]Loading assembly " + _moduleDef.FullName);
                _asmPath = args[0];
                if (args.Length > 1)
                    _verbose = args[1] == "-v";
            }
            catch (Exception ex) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[!] Verbose mode can be activated with -v");
                Console.WriteLine("[!] Error: Cannot load the file. Make sure it's a valid .NET file!");
                Console.WriteLine($"[!] StackTrace: {ex.StackTrace}");
                Console.ForegroundColor = ConsoleColor.White;
                Console.ReadKey();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                "[!] Trying to Restore Methods from VM - for best results move VM Restore to target folder!");
            Console.ForegroundColor = ConsoleColor.White;

            // fix for dll's needed
            Environment.CurrentDirectory = Path.GetDirectoryName(_asmPath);

            var restoreDynamicMethods = new RestoreDynamicMethods(_moduleDef, _asmPath, _verbose);
            var restoredMethods = restoreDynamicMethods.Proccess();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[!] Restored {restoredMethods} methods from VM");
            Console.ForegroundColor = ConsoleColor.White;
            //save module
            var outputPath =
                $"{Path.GetDirectoryName(args[0])}\\{Path.GetFileNameWithoutExtension(args[0])}_patched{Path.GetExtension(args[0])}";

            var moduleWriterOptions = new ModuleWriterOptions(_moduleDef) {
                MetaDataOptions = { Flags = MetaDataFlags.PreserveAll }
            };

            try {
                _moduleDef.Write(outputPath, moduleWriterOptions);
            }
            catch (ModuleWriterException) {
                moduleWriterOptions.Logger = DummyLogger.NoThrowInstance;
                _moduleDef.Write(outputPath, moduleWriterOptions);
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[!] Assembly saved");
            Console.ForegroundColor = ConsoleColor.White;
            Console.ReadKey();
        }
    }
}
