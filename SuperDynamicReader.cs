using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.IO;
using ExceptionHandler = dnlib.DotNet.Emit.ExceptionHandler;
using SR = System.Reflection;

namespace BabelVMRestore {
    /// <summary>
    /// Reads code from a DynamicMethod
    /// </summary>
    internal class SuperDynamicReader : MethodBodyReaderBase, ISignatureReaderHelper {
        static readonly ReflectionFieldInfo RtdmOwnerFieldInfo = new ReflectionFieldInfo("m_owner");
        static readonly ReflectionFieldInfo DmResolverFieldInfo = new ReflectionFieldInfo("m_resolver");
        static readonly ReflectionFieldInfo RslvCodeFieldInfo = new ReflectionFieldInfo("m_code");
        static readonly ReflectionFieldInfo RslvDynamicScopeFieldInfo = new ReflectionFieldInfo("m_scope");
        static readonly ReflectionFieldInfo RslvMethodFieldInfo = new ReflectionFieldInfo("m_method");
        static readonly ReflectionFieldInfo RslvLocalsFieldInfo = new ReflectionFieldInfo("m_localSignature");
        static readonly ReflectionFieldInfo RslvMaxStackFieldInfo = new ReflectionFieldInfo("m_stackSize");
        static readonly ReflectionFieldInfo RslvExceptionsFieldInfo = new ReflectionFieldInfo("m_exceptions");
        static readonly ReflectionFieldInfo RslvExceptionHeaderFieldInfo = new ReflectionFieldInfo("m_exceptionHeader");
        static readonly ReflectionFieldInfo ScopeTokensFieldInfo = new ReflectionFieldInfo("m_tokens");
        static readonly ReflectionFieldInfo GfiFieldHandleFieldInfo = new ReflectionFieldInfo("m_field", "m_fieldHandle");
        static readonly ReflectionFieldInfo GfiContextFieldInfo = new ReflectionFieldInfo("m_context");

        static readonly ReflectionFieldInfo GmiMethodHandleFieldInfo = new ReflectionFieldInfo("m_method",
            "m_methodHandle");

        static readonly ReflectionFieldInfo GmiContextFieldInfo = new ReflectionFieldInfo("m_context");
        static readonly ReflectionFieldInfo EhCatchAddrFieldInfo = new ReflectionFieldInfo("m_catchAddr");
        static readonly ReflectionFieldInfo EhCatchClassFieldInfo = new ReflectionFieldInfo("m_catchClass");
        static readonly ReflectionFieldInfo EhCatchEndAddrFieldInfo = new ReflectionFieldInfo("m_catchEndAddr");
        static readonly ReflectionFieldInfo EhCurrentCatchFieldInfo = new ReflectionFieldInfo("m_currentCatch");
        static readonly ReflectionFieldInfo EhTypeFieldInfo = new ReflectionFieldInfo("m_type");
        static readonly ReflectionFieldInfo EhStartAddrFieldInfo = new ReflectionFieldInfo("m_startAddr");
        static readonly ReflectionFieldInfo EhEndAddrFieldInfo = new ReflectionFieldInfo("m_endAddr");
        static readonly ReflectionFieldInfo EhEndFinallyFieldInfo = new ReflectionFieldInfo("m_endFinally");
        static readonly ReflectionFieldInfo VamMethodFieldInfo = new ReflectionFieldInfo("m_method");
        static readonly ReflectionFieldInfo VamDynamicMethodFieldInfo = new ReflectionFieldInfo("m_dynamicMethod");
        static readonly ReflectionFieldInfo MethodDynamicInfo = new ReflectionFieldInfo("m_DynamicILInfo");
        readonly ModuleDef _module;
        Importer _importer;
        readonly GenericParamContext _gpContext;
        MethodDef _method;
        int _codeSize;
        int _maxStack;
        List<object> _tokens;
        readonly IList<object> _ehInfos;
        byte[] _ehHeader;

        class ReflectionFieldInfo {
            SR.FieldInfo _fieldInfo;
            readonly string _fieldName1;
            readonly string _fieldName2;

            public ReflectionFieldInfo(string fieldName) {
                _fieldName1 = fieldName;
            }

            public ReflectionFieldInfo(string fieldName1, string fieldName2) {
                _fieldName1 = fieldName1;
                _fieldName2 = fieldName2;
            }

            public object Read(object instance) {
                if (_fieldInfo == null)
                    InitializeField(instance.GetType());
                if (_fieldInfo == null)
                    throw new Exception($"Couldn't find field '{_fieldName1}' or '{_fieldName2}'");

                return _fieldInfo.GetValue(instance);
            }

            public bool Exists(object instance) {
                InitializeField(instance.GetType());
                return _fieldInfo != null;
            }

            void InitializeField(SR.IReflect type) {
                if (_fieldInfo != null)
                    return;

                const SR.BindingFlags flags =
                    SR.BindingFlags.Instance | SR.BindingFlags.Public | SR.BindingFlags.NonPublic;
                _fieldInfo = type.GetField(_fieldName1, flags);
                if (_fieldInfo == null && _fieldName2 != null)
                    _fieldInfo = type.GetField(_fieldName2, flags);
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="module">Module that will own the method body</param>
        /// <param name="obj">This can be one of several supported types: the delegate instance
        /// created by DynamicMethod.CreateDelegate(), a DynamicMethod instance, a RTDynamicMethod
        /// instance or a DynamicResolver instance.</param>
        public SuperDynamicReader(ModuleDef module, object obj)
            : this(module, obj, new GenericParamContext()) {}

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="module">Module that will own the method body</param>
        /// <param name="obj">This can be one of several supported types: the delegate instance
        /// created by DynamicMethod.CreateDelegate(), a DynamicMethod instance, a RTDynamicMethod
        /// instance or a DynamicResolver instance.</param>
        /// <param name="gpContext">Generic parameter context</param>
        public SuperDynamicReader(ModuleDef module, object obj, GenericParamContext gpContext) {
            _module = module;
            _importer = new Importer(module, ImporterOptions.TryToUseDefs, gpContext);
            _gpContext = gpContext;

            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            var del = obj as Delegate;
            if (del != null) {
                obj = del.Method;
                if (obj == null)
                    throw new Exception("Delegate.Method == null");
            }

            if (obj.GetType().ToString() == "System.Reflection.Emit.DynamicMethod+RTDynamicMethod") {
                obj = RtdmOwnerFieldInfo.Read(obj) as DynamicMethod;
                if (obj == null)
                    throw new Exception("RTDynamicMethod.m_owner is null or invalid");
            }

            if (obj is DynamicMethod) {
                var obj2 = obj;
                obj = DmResolverFieldInfo.Read(obj);
                if (obj == null) {
                    // could be compiled from dynamic info instead of raw shit
                    obj = obj2;
                    obj = MethodDynamicInfo.Read(obj);
                    if (obj == null)
                        throw new Exception("No resolver found");

                    SecondOption(obj);
                    return;
                }
            }

            if (obj.GetType().ToString() != "System.Reflection.Emit.DynamicResolver")
                throw new Exception("Couldn't find DynamicResolver");

            var code = RslvCodeFieldInfo.Read(obj) as byte[];
            if (code == null)
                throw new Exception("No code");
            _codeSize = code.Length;
            var delMethod = RslvMethodFieldInfo.Read(obj) as SR.MethodBase;
            if (delMethod == null)
                throw new Exception("No method");
            _maxStack = (int)RslvMaxStackFieldInfo.Read(obj);

            var scope = RslvDynamicScopeFieldInfo.Read(obj);
            if (scope == null)
                throw new Exception("No scope");
            var tokensList = ScopeTokensFieldInfo.Read(scope) as IList;
            if (tokensList == null)
                throw new Exception("No tokens");
            _tokens = new List<object>(tokensList.Count);
            foreach (var token in tokensList)
                _tokens.Add(token);

            _ehInfos = (IList<object>)RslvExceptionsFieldInfo.Read(obj);
            _ehHeader = RslvExceptionHeaderFieldInfo.Read(obj) as byte[];

            UpdateLocals(RslvLocalsFieldInfo.Read(obj) as byte[]);
            reader = MemoryImageStream.Create(code);
            _method = CreateMethodDef(delMethod);
            parameters = _method.Parameters;
        }

        public static T GetFieldValue<T>(object obj, string fieldName) {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            var field = obj.GetType().GetField(fieldName, SR.BindingFlags.Public |
                                                          SR.BindingFlags.NonPublic |
                                                          SR.BindingFlags.Instance);

            if (field == null)
                throw new ArgumentException("fieldName", nameof(obj));

            if (!typeof(T).IsAssignableFrom(field.FieldType))
                throw new InvalidOperationException("Field type and requested type are not compatible.");

            return (T)field.GetValue(obj);
        }

        void SecondOption(object obj) {
            // this is entirely different from the one below
            var code = GetFieldValue<byte[]>(obj, "m_code");
            if (code == null)
                throw new Exception("No code");
            _codeSize = code.Length;
            var delMethod = GetFieldValue<SR.MethodBase>(obj, "m_method");
            if (delMethod == null)
                throw new Exception("No method");
            _maxStack = GetFieldValue<int>(obj, "m_maxStackSize");

            var scope = GetFieldValue<object>(obj, "m_scope");
            if (scope == null)
                throw new Exception("No scope");
            var tokensList = GetFieldValue<IList>(scope, "m_tokens");
            if (tokensList == null)
                throw new Exception("No tokens");
            _tokens = new List<object>(tokensList.Count);
            foreach (var oken in tokensList)
                _tokens.Add(oken);

            //ehInfos = (IList<object>)rslvExceptionsFieldInfo.Read(obj);
            _ehHeader = GetFieldValue<byte[]>(obj, "m_exceptions");

            UpdateLocals(GetFieldValue<byte[]>(obj, "m_localSignature"));
            reader = MemoryImageStream.Create(code);
            _method = CreateMethodDef(delMethod);
            parameters = _method.Parameters;
        }

        class ExceptionInfo {
            public int[] CatchAddr;
            public Type[] CatchClass;
            public int[] CatchEndAddr;
            public int CurrentCatch;
            public int[] Type;
            public int StartAddr;
            public int EndAddr;
            public int EndFinally;
        }

        static IEnumerable<ExceptionInfo> CreateExceptionInfos(ICollection<object> ehInfos) {
            if (ehInfos == null)
                return new List<ExceptionInfo>();

            var infos = new List<ExceptionInfo>(ehInfos.Count);
            infos.AddRange(ehInfos.Select(ehInfo => new ExceptionInfo {
                CatchAddr = (int[])EhCatchAddrFieldInfo.Read(ehInfo),
                CatchClass = (Type[])EhCatchClassFieldInfo.Read(ehInfo),
                CatchEndAddr = (int[])EhCatchEndAddrFieldInfo.Read(ehInfo),
                CurrentCatch = (int)EhCurrentCatchFieldInfo.Read(ehInfo),
                Type = (int[])EhTypeFieldInfo.Read(ehInfo),
                StartAddr = (int)EhStartAddrFieldInfo.Read(ehInfo),
                EndAddr = (int)EhEndAddrFieldInfo.Read(ehInfo),
                EndFinally = (int)EhEndFinallyFieldInfo.Read(ehInfo)
            }));

            return infos;
        }

        void UpdateLocals(byte[] localsSig) {
            if (localsSig == null || localsSig.Length == 0)
                return;

            var sig = SignatureReader.ReadSig(this, _module.CorLibTypes, localsSig, _gpContext) as LocalSig;
            if (sig == null)
                return;

            foreach (var local in sig.Locals)
                locals.Add(new Local(local));
        }

        MethodDef CreateMethodDef(SR.MethodBase delMethod) {
            var method = new MethodDefUser();

            var retType = GetReturnType(delMethod);
            var pms = GetParameters(delMethod);
            method.Signature = MethodSig.CreateStatic(retType, pms.ToArray());

            method.Parameters.UpdateParameterTypes();
            method.ImplAttributes = MethodImplAttributes.IL;
            method.Attributes = MethodAttributes.PrivateScope;
            method.Attributes |= MethodAttributes.Static;

            return _module.UpdateRowId(method);
        }

        TypeSig GetReturnType(SR.MethodBase mb) {
            var mi = mb as SR.MethodInfo;
            return mi != null ? _importer.ImportAsTypeSig(mi.ReturnType) : _module.CorLibTypes.Void;
        }

        List<TypeSig> GetParameters(SR.MethodBase delMethod) {
            return delMethod.GetParameters().Select(param => _importer.ImportAsTypeSig(param.ParameterType)).ToList();
        }

        /// <summary>
        /// Reads the code
        /// </summary>
        /// <returns></returns>
        public bool Read() {
            ReadInstructionsNumBytes((uint)_codeSize);
            CreateExceptionHandlers();

            return true;
        }

        void CreateExceptionHandlers() {
            if (_ehHeader != null && _ehHeader.Length != 0) {
                var binaryReader = new BinaryReader(new MemoryStream(_ehHeader));
                var b = binaryReader.ReadByte();
                if ((b & 0x40) == 0) {
                    // DynamicResolver only checks bit 6
                    // Calculate num ehs exactly the same way that DynamicResolver does
                    int numHandlers = (ushort)((binaryReader.ReadByte() - 2) / 12);
                    binaryReader.ReadInt16();
                    for (var i = 0; i < numHandlers; i++) {
                        var eh = new ExceptionHandler { HandlerType = (ExceptionHandlerType)binaryReader.ReadInt16() };
                        int offs = binaryReader.ReadUInt16();
                        eh.TryStart = GetInstructionThrow((uint)offs);
                        eh.TryEnd = GetInstruction((uint)(binaryReader.ReadSByte() + offs));
                        offs = binaryReader.ReadUInt16();
                        eh.HandlerStart = GetInstructionThrow((uint)offs);
                        eh.HandlerEnd = GetInstruction((uint)(binaryReader.ReadSByte() + offs));

                        switch (eh.HandlerType) {
                            case ExceptionHandlerType.Catch:
                                eh.CatchType = ReadToken(binaryReader.ReadUInt32()) as ITypeDefOrRef;
                                break;
                            case ExceptionHandlerType.Filter:
                                eh.FilterStart = GetInstruction(binaryReader.ReadUInt32());
                                break;
                            default:
                                binaryReader.ReadUInt32();
                                break;
                        }

                        exceptionHandlers.Add(eh);
                    }
                }
                else {
                    binaryReader.BaseStream.Position--;
                    int numHandlers = (ushort)(((binaryReader.ReadUInt32() >> 8) - 4) / 24);
                    for (var i = 0; i < numHandlers; i++) {
                        var eh = new ExceptionHandler { HandlerType = (ExceptionHandlerType)binaryReader.ReadInt32() };
                        var offs = binaryReader.ReadInt32();
                        eh.TryStart = GetInstructionThrow((uint)offs);
                        eh.TryEnd = GetInstruction((uint)(binaryReader.ReadInt32() + offs));
                        offs = binaryReader.ReadInt32();
                        eh.HandlerStart = GetInstructionThrow((uint)offs);
                        eh.HandlerEnd = GetInstruction((uint)(binaryReader.ReadInt32() + offs));

                        switch (eh.HandlerType) {
                            case ExceptionHandlerType.Catch:
                                eh.CatchType = ReadToken(binaryReader.ReadUInt32()) as ITypeDefOrRef;
                                break;
                            case ExceptionHandlerType.Filter:
                                eh.FilterStart = GetInstruction(binaryReader.ReadUInt32());
                                break;
                            default:
                                binaryReader.ReadUInt32();
                                break;
                        }

                        exceptionHandlers.Add(eh);
                    }
                }
            }
            else if (_ehInfos != null) {
                foreach (var ehInfo in CreateExceptionInfos(_ehInfos)) {
                    var tryStart = GetInstructionThrow((uint)ehInfo.StartAddr);
                    var tryEnd = GetInstruction((uint)ehInfo.EndAddr);
                    var endFinally = ehInfo.EndFinally < 0 ? null : GetInstruction((uint)ehInfo.EndFinally);
                    for (var i = 0; i < ehInfo.CurrentCatch; i++) {
                        var eh = new ExceptionHandler {
                            HandlerType = (ExceptionHandlerType)ehInfo.Type[i],
                            TryStart = tryStart
                        };
                        eh.TryEnd = eh.HandlerType == ExceptionHandlerType.Finally ? endFinally : tryEnd;
                        eh.FilterStart = null; // not supported by DynamicMethod.ILGenerator
                        eh.HandlerStart = GetInstructionThrow((uint)ehInfo.CatchAddr[i]);
                        eh.HandlerEnd = GetInstruction((uint)ehInfo.CatchEndAddr[i]);
                        eh.CatchType = _importer.Import(ehInfo.CatchClass[i]);
                        exceptionHandlers.Add(eh);
                    }
                }
            }
        }

        /// <summary>
        /// Returns the created method. Must be called after <see cref="Read()"/>.
        /// </summary>
        /// <returns>A new <see cref="CilBody"/> instance</returns>
        public MethodDef GetMethod() {
            var cilBody = new CilBody(true, instructions, exceptionHandlers, locals) {
                MaxStack = (ushort)Math.Min(_maxStack, ushort.MaxValue)
            };
            instructions = null;
            exceptionHandlers = null;
            locals = null;
            _method.Body = cilBody;
            return _method;
        }

        /// <inheritdoc/>
        protected override IField ReadInlineField(Instruction instr) {
            return ReadToken(reader.ReadUInt32()) as IField;
        }

        /// <inheritdoc/>
        protected override IMethod ReadInlineMethod(Instruction instr) {
            return ReadToken(reader.ReadUInt32()) as IMethod;
        }

        /// <inheritdoc/>
        protected override MethodSig ReadInlineSig(Instruction instr) {
            return ReadToken(reader.ReadUInt32()) as MethodSig;
        }

        /// <inheritdoc/>
        protected override string ReadInlineString(Instruction instr) {
            return ReadToken(reader.ReadUInt32()) as string ?? string.Empty;
        }

        /// <inheritdoc/>
        protected override ITokenOperand ReadInlineTok(Instruction instr) {
            return ReadToken(reader.ReadUInt32()) as ITokenOperand;
        }

        /// <inheritdoc/>
        protected override ITypeDefOrRef ReadInlineType(Instruction instr) {
            return ReadToken(reader.ReadUInt32()) as ITypeDefOrRef;
        }

        object ReadToken(uint token) {
            var rid = token & 0x00FFFFFF;
            switch (token >> 24) {
                case 0x02:
                    return ImportType(rid);

                case 0x04:
                    return ImportField(rid);

                case 0x06:
                case 0x0A:
                    return ImportMethod(rid);

                case 0x11:
                    return ImportSignature(rid);

                case 0x70:
                    return Resolve(rid) as string;

                default:
                    return null;
            }
        }

        IMethod ImportMethod(uint rid) {
            var obj = Resolve(rid);
            if (obj == null)
                return null;

            if (obj is RuntimeMethodHandle)
                return _importer.Import(SR.MethodBase.GetMethodFromHandle((RuntimeMethodHandle)obj));

            switch (obj.GetType().ToString()) {
                case "System.Reflection.Emit.GenericMethodInfo": {
                    var context = (RuntimeTypeHandle)GmiContextFieldInfo.Read(obj);
                    var method =
                        SR.MethodBase.GetMethodFromHandle((RuntimeMethodHandle)GmiMethodHandleFieldInfo.Read(obj),
                            context);
                    return _importer.Import(method);
                }
                case "System.Reflection.Emit.VarArgMethod": {
                    var method = GetVarArgMethod(obj);
                    if (!(method is DynamicMethod))
                        return _importer.Import(method);
                    obj = method;
                }
                    break;
            }

            var dm = obj as DynamicMethod;
            if (dm != null)
                throw new Exception("DynamicMethod calls another DynamicMethod");

            return null;
        }

        static SR.MethodInfo GetVarArgMethod(object obj) {
            // .NET 2.0
            // This is either a DynamicMethod or a MethodInfo
            if (!VamDynamicMethodFieldInfo.Exists(obj)) return VamMethodFieldInfo.Read(obj) as SR.MethodInfo;
            // .NET 4.0+
            var method = VamMethodFieldInfo.Read(obj) as SR.MethodInfo;
            var dynMethod = VamDynamicMethodFieldInfo.Read(obj) as DynamicMethod;
            return dynMethod ?? method;
        }

        IField ImportField(uint rid) {
            var obj = Resolve(rid);
            if (obj == null)
                return null;

            if (obj is RuntimeFieldHandle)
                return _importer.Import(SR.FieldInfo.GetFieldFromHandle((RuntimeFieldHandle)obj));

            if (obj.GetType().ToString() != "System.Reflection.Emit.GenericFieldInfo") return null;
            var context = (RuntimeTypeHandle)GfiContextFieldInfo.Read(obj);
            var field = SR.FieldInfo.GetFieldFromHandle((RuntimeFieldHandle)GfiFieldHandleFieldInfo.Read(obj), context);
            return _importer.Import(field);
        }

        ITypeDefOrRef ImportType(uint rid) {
            var obj = Resolve(rid);
            if (obj is RuntimeTypeHandle)
                return _importer.Import(Type.GetTypeFromHandle((RuntimeTypeHandle)obj));

            return null;
        }

        CallingConventionSig ImportSignature(uint rid) {
            var sig = Resolve(rid) as byte[];
            return sig == null ? null : SignatureReader.ReadSig(this, _module.CorLibTypes, sig, _gpContext);
        }

        object Resolve(uint index) {
            return index >= (uint)_tokens.Count ? null : _tokens[(int)index];
        }

        ITypeDefOrRef ISignatureReaderHelper.ResolveTypeDefOrRef(uint codedToken, GenericParamContext gpContext) {
            uint token;
            if (!CodedToken.TypeDefOrRef.Decode(codedToken, out token))
                return null;
            var rid = MDToken.ToRID(token);
            switch (MDToken.ToTable(token)) {
                case Table.TypeDef:
                case Table.TypeRef:
                case Table.TypeSpec:
                    return ImportType(rid);
            }
            return null;
        }

        TypeSig ISignatureReaderHelper.ConvertRTInternalAddress(IntPtr address) {
            return _importer.ImportAsTypeSig(MethodTableToTypeConverter.Convert(address));
        }
    }
}
