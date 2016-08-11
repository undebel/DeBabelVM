using System;
using System.Reflection;

namespace BabelVMRestore {
    internal class Helpers {
        internal static object GetInstanceField(Type type, object instance, string fieldName) {
            const BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Static;
            return type.GetField(fieldName, bindFlags)?.GetValue(instance);
        }
    }
}
