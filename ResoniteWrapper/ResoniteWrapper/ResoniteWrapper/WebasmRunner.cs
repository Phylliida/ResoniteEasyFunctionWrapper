using HarmonyLib; // HarmonyLib comes included with a ResoniteModLoader install
using ResoniteModLoader;
using System;
using System.Reflection;
using ProtoFlux.Core;
using ProtoFlux.Runtimes.Execution;
using System.Runtime.CompilerServices;
using Elements.Core;
using Wasmtime;
using FrooxEngine;
using SkyFrost.Base;
using System.Security.Policy;
using System.Collections.Generic;
using ResoniteHotReloadLib;
using System.ComponentModel;
using System.Net;
using ProtoFlux.Runtimes.Execution.Nodes;
using FrooxEngine.ProtoFlux;
using ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
// tmp

namespace RefhackCasts
{
    public class SimpleMod
    {
        public void Bees(string wow, int bees, float ok, Slot item, 
            out Dictionary<String, String> out1, out float out2, out Grabbable out3)
        {
            out1 = new Dictionary<string, string>();
            if (wow != null)
            {
                out1[wow] = bees.ToString();
            }
            out2 = ok;
            out3 = null;
            if (item != null)
            {
                out3 = item.GetComponent<Grabbable>();
            }
        }

        public string readFromDict(Dictionary<string, string> dict, string key)
        {
            return dict[key];
        }
    }

    public enum VariableKind
    {
        Parameter,
        TupleFromReturn,
        ReturnValue
    }

    public struct VariableInfo
    {
        public Type type;
        public string name;
        public VariableKind variableKind;
        public int paramIndex;
        public bool isValidResoniteType;
        public Type resoniteType;
        public VariableInfo(string name, Type type, VariableKind variableKind, int paramIndex=0)
        {
            this.name = name;
            this.type = type;
            this.variableKind = variableKind;
            this.paramIndex = paramIndex;
            this.isValidResoniteType = FrooxEngine.Engine.Current.WorldManager.FocusedWorld.Types.IsSupported(type);
            this.resoniteType = this.isValidResoniteType ? type : typeof(string);
        }
    }


    public class WebasmRunner : ResoniteMod
    {
        public class RollingCache
        {
            public int offset;
            public System.Object[] values;
            public Guid[] keys;

            public RollingCache(int capacity)
            {
                values = new object[capacity];
                keys = new Guid[capacity];
            }

            public bool TryLookup(System.Object value, out Guid guid)
            {
                guid = Guid.Empty;
                for (int i = 0; i < values.Length; i++ )
                {
                    if (values[i] == value)
                    {
                        guid = keys[i];
                        return true;
                    }
                }
                return false;
            }

            public void Add(System.Object value, Guid key)
            {
                values[offset] = value;
                keys[offset] = key;
                offset = (offset + 1) % values.Length;
            }
        }

        public class UnsupportedTypeLookup
        {
            // maintain a cache/lookup for each type, this ensures caches don't get flooded out by other types
            // and so prevents entry creation spam
            int cacheCapacity;
            Dictionary<Type, UnsupportedTypeLookupHelper> lookups = new Dictionary<Type, UnsupportedTypeLookupHelper>();
            public UnsupportedTypeLookup(int cacheCapacity)
            {
                this.cacheCapacity = cacheCapacity;
            }

            UnsupportedTypeLookupHelper GetHelperForType(Type type)
            {
                UnsupportedTypeLookupHelper lookup;
                if (!lookups.TryGetValue(type, out lookup))
                {
                    lookup = new UnsupportedTypeLookupHelper(cacheCapacity);
                    lookups[type] = lookup;
                }
                return lookup;
            }

            public Guid Add(System.Object value)
            {
                return GetHelperForType(value.GetType()).Add(value);
            }

            public bool TryGet(Guid guid, Type type, out System.Object value)
            {
                return GetHelperForType(type).TryGet(guid, out value);
            }
        }

        public class UnsupportedTypeLookupHelper
        {
            RollingCache cache;
            public Dictionary<Guid, System.Object> lookup = new Dictionary<Guid, object>();

            public UnsupportedTypeLookupHelper(int cacheCapacity)
            {
                cache = new RollingCache(cacheCapacity);
            }

            public Guid Add(System.Object value)
            {
                Guid guid;
                // we have a cache so the first 10 or so values won't allocate new dict entries every call
                if (!cache.TryLookup(value, out guid))
                {
                    guid = Guid.NewGuid();
                    while (lookup.ContainsKey(guid))
                    {
                        guid = Guid.NewGuid();
                    }
                    lookup[guid] = value;
                    cache.Add(value, guid);
                }
                return guid;
            }

            public bool TryGet(Guid guid, out System.Object value)
            {
                return lookup.TryGetValue(guid, out value);
            }
        }

        public static void GetMethodVars(MethodInfo method, out List<VariableInfo> inputVars, out List<VariableInfo> returnVars)
        {
            inputVars = new List<VariableInfo>();
            returnVars = new List<VariableInfo>();
            // Modified from https://stackoverflow.com/a/28772413
            Type returnType = method.ReturnType;
            if (returnType.IsGenericType)
            {
                var genType = returnType.GetGenericTypeDefinition();
                if (genType == typeof(Tuple<>)
                    || genType == typeof(Tuple<,>)
                    || genType == typeof(Tuple<,,>)
                    || genType == typeof(Tuple<,,,>)
                    || genType == typeof(Tuple<,,,,>)
                    || genType == typeof(Tuple<,,,,,>)
                    || genType == typeof(Tuple<,,,,,,>)
                    || genType == typeof(Tuple<,,,,,,,>))
                {
                    for (var i = 0; i < returnType.GetGenericArguments().Length; i++)
                    {
                        returnVars.Add(new VariableInfo(i.ToString(), returnType.GetGenericArguments()[i], VariableKind.TupleFromReturn, i));
                    }
                }
                else
                {
                    returnVars.Add(new VariableInfo("0", returnType, VariableKind.ReturnValue));
                }
            }
            else
            {
                returnVars.Add(new VariableInfo("0", returnType, VariableKind.ReturnValue));
            }

            ParameterInfo[] methodParams = method.GetParameters();

            for (int i = 0; i < methodParams.Length; i++)
            {
                ParameterInfo param = methodParams[i];
                // out type
                if (param.ParameterType.IsByRef && param.IsOut)
                {
                    returnVars.Add(new VariableInfo(param.Name, param.ParameterType, VariableKind.Parameter, paramIndex: i));
                }
                // ref type, its an input and output
                else if (param.ParameterType.IsByRef && !param.IsOut)
                {
                    inputVars.Add(new VariableInfo(param.Name, param.ParameterType, VariableKind.Parameter, paramIndex: i));
                    returnVars.Add(new VariableInfo(param.Name, param.ParameterType, VariableKind.Parameter, paramIndex: i));
                }
                // input type
                else if (!param.IsOut)
                {
                    inputVars.Add(new VariableInfo(param.Name, param.ParameterType, VariableKind.Parameter, paramIndex: i));
                }
            }
        }

        public class WrappedMethod
        {
            MethodInfo method;
            string name;
            List<VariableInfo> inputVars;
            List<VariableInfo> returnVars;

            public WrappedMethod(MethodInfo method)
            {
                this.method = method;
                this.name = method.Name;
                GetMethodVars(method, out inputVars, out returnVars);
            }



            public void CallMethod(Slot dataSlot, UnsupportedTypeLookup typeLookup)
            {
                bool success = true;
                string error = "";
                DynamicVariableSpace space = dataSlot.GetComponent<DynamicVariableSpace>();
                Object[] parameters = new object[method.GetParameters().Length];
                object[] dummyParams = new object[2] { null, null };
                foreach (VariableInfo inputVar in inputVars)
                {
                    // Cursed stuff to call of generic type
                    MethodInfo readMethod = typeof(DynamicVariableSpace).GetMethod(nameof(space.TryReadValue));
                    MethodInfo genericReadMethod = readMethod.MakeGenericMethod(inputVar.type);
                    // the null at second place works as an out
                    dummyParams[0] = inputVar.name;
                    dummyParams[1] = null;
                    bool found = (bool)genericReadMethod.Invoke(space, dummyParams);
                    object value = dummyParams[1];
                    if (found)
                    {
                        if (inputVar.isValidResoniteType)
                        {
                            parameters[inputVar.paramIndex] = value;
                        }
                        else
                        {
                            // if not a valid resonite type we need to use our lookup table to find the value
                            // because we just store a string with a guid pointing to it
                            System.Object nonResoniteValue;
                            Guid inputParamGuid;
                            if(Guid.TryParse((string)value, out inputParamGuid) &&
                                typeLookup.TryGet(inputParamGuid, inputVar.type, out nonResoniteValue)) {
                                parameters[inputVar.paramIndex] = nonResoniteValue;
                            }
                            else
                            {
                                success = false;
                                error = "Failed to read parameter " + inputVar.name + " with non resonite type " + inputVar.type + " the guid of " + inputParamGuid + " does not exist in lookup";
                            }
                        }
                    }
                    else
                    {
                        error = "In Dynvar, could not find parameter with name " + inputVar.name + " with type " + inputVar.resoniteType;
                        success = false;
                        break;
                    }
                }

                if (success)
                {
                    // null for first input to Invoke means static, we only support static methods
                    var result = this.method.Invoke(null, parameters);

                    foreach (VariableInfo returnVar in returnVars)
                    {
                        object value = null;

                        if (returnVar.variableKind == VariableKind.Parameter)
                        {
                            value = parameters[returnVar.paramIndex];
                        }
                        else if(returnVar.variableKind == VariableKind.ReturnValue)
                        {
                            value = result;
                        }
                        else if(returnVar.variableKind == VariableKind.TupleFromReturn)
                        {
                            if (result == null)
                            {
                                success = false;
                                error = "Expected tuple, returned null";
                            }
                            else
                            {
                                ITuple resultTuple = result as ITuple;
                                if (resultTuple == null)
                                {
                                    success = false;
                                    error = "Expected tuple, returned value of type " + result.GetType().ToString();
                                }
                                else
                                {
                                    value = resultTuple[returnVar.paramIndex];
                                }
                            }
                        }
                        if (success)
                        {
                            // if not a valid resonite type we need to convert to string using our lookup table
                            if (!returnVar.isValidResoniteType)
                            {
                                // We don't want to allocate a new uuid if we are just passing through a value or returning a constant value
                                // this has a small cache for each type encountered to prevent that
                                value = typeLookup.Add(value).ToString();
                            }
                            // Cursed stuff to call of generic type
                            MethodInfo writeMethod = typeof(DynamicVariableSpace).GetMethod(nameof(space.TryWriteValue));
                            MethodInfo genericWriteMethod = writeMethod.MakeGenericMethod(returnVar.resoniteType);
                            dummyParams[0] = returnVar.name;
                            dummyParams[1] = value;
                            bool written = (bool)genericWriteMethod.Invoke(space, dummyParams);
                            if (!written)
                            {
                                success = false;
                                error = "Failed to write to output value " + returnVar.name + " of type " + returnVar.type.ToString();
                            }
                        }                        
                        if (!success)
                        {
                            break;
                        }
                    }
                }
            }

            
            public void CreateTemplateObject()
            {
                
            }
            public void CreateHooks()
            {
                
            }
        }

        public static List<bool> GetSupportedTypes(List<Type> types)
        {
            List<bool> supported = new List<bool>();
            foreach (Type type in types)
            {
                supported.Add(FrooxEngine.Engine.Current.WorldManager.FocusedWorld.Types.IsSupported(type));
            }
            return supported;
        }

        public static void WrapClass(Type classType)
        {
            World world = ;

            foreach (MethodInfo method in classType.GetMethods())
            {
            }
            FrooxEngine.Engine.Current.WorldManager.FocusedWorld.LocalUserSpace.AddSlot("")
            Engine.Current.WorldManager.FocusedWorld.RootSlot;
        }

        // I should make a general purpose thing
        // They should all share engine
        // but should be able to create store and linker
        // to call methods on linker
        // etc.
        private static Wasmtime.Engine engine;
        private static Wasmtime.Linker linker;
        private static Wasmtime.Store store;
        private static System.Collections.Generic.Dictionary<System.Guid, Wasmtime.Module> moduleLookup;
        public override string Name => "RefhackCasts";
        public override string Author => "TessaCoil";
        public override string Version => "1.0.0"; //Version of the mod, should match the AssemblyVersion
        public override string Link => "https://github.com/Phylliida/RefhackCasts"; // Optional link to a repo where this mod would be located

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> enabled = new ModConfigurationKey<bool>("enabled", "Should the mod be enabled", () => true); //Optional config settings

        private static ModConfiguration Config; //If you use config settings, this will be where you interface with them.
        private static string harmony_id = "bepis.Phylliida.WebasmRunner";

        private static Harmony harmony;
        public override void OnEngineInit()
        {
            HotReloader.RegisterForHotReload(this);

            Config = GetConfiguration(); //Get the current ModConfiguration for this mod
            Config.Save(true); //If you'd like to save the default config values to file
        
            SetupMod();
        }

        public static void SetupMod()
        {
            harmony = new Harmony(harmony_id); //typically a reverse domain name is used here (https://en.wikipedia.org/wiki/Reverse_domain_name_notation)
            harmony.PatchAll(); // do whatever LibHarmony patching you need, this will patch all [HarmonyPatch()] instances
          
            Msg("applied wasm patches successfully");
            //Various log methods provided by the mod loader, below is an example of how they will look
            //3:14:42 AM.069 ( -1 FPS)  [INFO] [ResoniteModLoader/ExampleMod] a regular log
            if (engine == null)
            {
                engine = new Wasmtime.Engine();
                linker = new Wasmtime.Linker(engine);
                store = new Wasmtime.Store(engine);
                Msg("started wasmtime successfully");
                moduleLookup = new System.Collections.Generic.Dictionary<Guid, Wasmtime.Module>();
            }
        }
        static void BeforeHotReload()
        {
            harmony = new Harmony(harmony_id);
            // This runs in the current assembly (i.e. the assembly which invokes the Hot Reload)
            harmony.UnpatchAll();
            if (engine != null)
            {
                engine.Dispose();
                engine = null;
                linker.Dispose();
                linker = null;
                store.Dispose();
                store = null;
                foreach (KeyValuePair<Guid, Wasmtime.Module> module in moduleLookup)
                {
                    module.Value.Dispose();
                }
                moduleLookup = null;
            }
            // This is where you unload your mod, free up memory, and remove Harmony patches etc.
        }

        static void OnHotReload(ResoniteMod modInstance)
        {
            // This runs in the new assembly (i.e. the one which was loaded fresh for the Hot Reload)
            
            // Get the config
            Config = modInstance.GetConfiguration();

            // Now you can setup your mod again
            SetupMod();
        }

        public enum WasmtimeMethod
        {
            AllocateMemory
        }
        

        // Should have:
        // low level api: just call websocket message send
        // data is in dynvar on slot websocket is on
        // everything happens sync
        
        // optional async option
        
        // high level api: 
        // if u want async, put poll on websocket dynvar state

        class CallWebasm
        {

            [JsonConverter(typeof(StringEnumConverter))]
            public WasmtimeMethod method;
        }

        [HarmonyPatch(typeof(WebsocketTextMessageSender), "RunAsync")]
        class WebsocketSendPatch
        {
            static bool Prefix(FrooxEngineContext context, System.Object __instance)
            {
                if (__instance.GetType() == typeof(WebsocketTextMessageSender))
                {
                    WebsocketTextMessageSender sender = (WebsocketTextMessageSender) __instance;
                    WebsocketClient websocketClient = sender.Client.Evaluate(context);
                    if (websocketClient == null)
                    {
                        return true;
                    }






                    DynamicVariableSpace dynvarSpace;
                    string wasmUuid;
                    Guid wasmGuid;
                    if (is_wasm_websocket(websocketClient, out dynvarSpace, out wasmUuid) &&
                        Guid.TryParse(wasmUuid, out wasmGuid) &&
                        moduleLookup.ContainsKey(wasmGuid))
                    {
                        Wasmtime.Module wasmModule = moduleLookup[wasmGuid];

                        string message = sender.Data.Evaluate(context);
                        JsonConvert.DeserializeObject<Dictionary<String, dynamic>(message);

                        Instance instance = linker.Instantiate(store, wasmModule);
                        instance.GetFunction("bees").Invoke(ValueBox)
                        
                    }
                }
                return true;
            }
        }


        static string WASM_URL = "wasm://wasm_internal/";
        static string WASM_INTERNAL_DYNVAR_SPACE = "WASM_INTERNAL";
        static string WASM_UUID = "uuid";
        static string WASM_FILE = "wasm";
        static string WASM_METADATA = "metadata";
        static string WASM_IMPORT_TEMPLATE = "importTemplate";
        static string WASM_EXPORT_TEMPLATE = "exportTemplate";
        static string WASM_EXPORT_SPACE = "WASM_EXPORT";
        static string WASM_EXPORT_NAME = "name";
        static string WASM_EXPORT_TYPE = "type";
        static string WASM_IMPORT_SPACE = "WASM_IMPORT";
        static string WASM_IMPORT_NAME = "name";
        static string WASM_IMPORT_TYPE = "type";

        static bool is_wasm_websocket(WebsocketClient client, out DynamicVariableSpace dynvarSpace, out string wasmUuid)
        {
            dynvarSpace = null;
            wasmUuid = null;
            Uri url = client.URL.Value;
            Msg("got url " + url.OriginalString);
            if (client.HandlingUser.Target == client.LocalUser &&
                url != null &&
                url.OriginalString == WASM_URL)
            {
                Msg("got wasm url " + url.OriginalString);
                dynvarSpace = client.Slot.GetComponent<DynamicVariableSpace>();
                if (dynvarSpace != null && dynvarSpace.SpaceName.Value == WASM_INTERNAL_DYNVAR_SPACE)
                {
                    if (dynvarSpace.TryReadValue<string>(WASM_UUID, out wasmUuid))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static void AttachValueKindToSlot(ValueKind valueKind, Slot slot, string valueKey, string isValueKey)
        {
            bool isValue;
            string valueKindStr = ValueKindToResoniteTypeString(valueKind, out isValue);

            DynamicValueVariable<string> valueDyn = slot.AttachComponent<DynamicValueVariable<string>>();
            valueDyn.VariableName.Value = valueKey;
            valueDyn.Value.Value = valueKindStr;

            DynamicValueVariable<bool> isValueDyn = slot.AttachComponent<DynamicValueVariable<bool>>();
            isValueDyn.VariableName.Value = isValueKey;
            isValueDyn.Value.Value = isValue;
        }

        static string ValueKindToResoniteTypeString(ValueKind valueKind, out bool isValueType)
        {
            isValueType = true;
            switch (valueKind) {
                case ValueKind.AnyRef: isValueType = false; return "AnyRef";
                case ValueKind.ExternRef: isValueType = false; return "ExternRef";
                case ValueKind.FuncRef: isValueType = false; return "FuncRef";
                case ValueKind.Float32: return "float";
                case ValueKind.Float64: return "double";
                case ValueKind.Int32: return "int";                
                case ValueKind.Int64: return "long";
                case ValueKind.V128: isValueType = false; return "vector";
            }
            throw new ArgumentException("Invalid value kind " + valueKind);
        }

        static void addValueStuff(Slot baseSlot, Slot templateSlot)
        {
            return;

            Type[] types = new Type[] {
                typeof(System.UInt64), typeof(System.Int64),
                typeof(System.UInt32), typeof(System.Int32),
                typeof(System.UInt16), typeof(System.Int16),
                typeof(System.Byte), typeof(System.SByte),
                typeof(System.Double), typeof(System.Single),
                typeof(Elements.Core.bool2), typeof(Elements.Core.bool3), typeof(Elements.Core.bool4),
                typeof(Elements.Core.uint2), typeof(Elements.Core.uint3), typeof(Elements.Core.uint4),
                typeof(Elements.Core.int2), typeof(Elements.Core.int3), typeof(Elements.Core.int4),
                typeof(Elements.Core.ulong2), typeof(Elements.Core.ulong3), typeof(Elements.Core.ulong4),
                typeof(Elements.Core.long2), typeof(Elements.Core.long3), typeof(Elements.Core.long4),
                typeof(Elements.Core.float2), typeof(Elements.Core.float3), typeof(Elements.Core.float4),
                typeof(Elements.Core.double2), typeof(Elements.Core.double3), typeof(Elements.Core.double4),
                typeof(Elements.Core.floatQ),
                typeof(Elements.Core.float2x2),
                typeof(Elements.Core.float3x3),
                typeof(Elements.Core.float4x4),
                typeof(Elements.Core.doubleQ),
                typeof(Elements.Core.double2x2),
                typeof(Elements.Core.double3x3),
                typeof(Elements.Core.double4x4),
                typeof(System.Decimal),
                typeof(System.Boolean),
                typeof(System.DateTime),
                typeof(System.TimeSpan),
                typeof(System.TimeSpan),
                typeof(Elements.Core.color),
                typeof(Elements.Core.colorX),
                typeof(Elements.Core.color32),
                typeof(FrooxEngine.ShadowCastMode),
                typeof(FrooxEngine.LightType),
                typeof(FrooxEngine.Key),
                typeof(HttpStatusCode),
                typeof(Type),
                typeof(HeadOutputDevice),
                typeof(CameraClearMode),
                typeof(CameraPositioningMode),
                typeof(CameraProjection),
                typeof(ZWrite),
                typeof(ZTest),
                typeof(Blend),
                typeof(BlendMode),
                typeof(Culling),
                typeof(BodyNode),
                typeof(Chirality),
                typeof(DummyEnum)
            };
            foreach (Type curType in types)
            {
                Slot typeSlot = templateSlot.Duplicate();
                typeSlot.NameField.Value = typeSlot.ToString();
                typeSlot.SetParent(baseSlot);
                typeSlot.RemoveAllComponents(x => true);
                Type valueWriteAdding = typeof(ValueWrite<>).MakeGenericType(typeof(FrooxEngine.ProtoFlux.FrooxEngineContext), curType);
                dynamic componentAdded = typeSlot.AttachComponent(valueWriteAdding);
                //typeSlot.AttachComponent()
                dynamic a = Convert.ChangeType(componentAdded, valueWriteAdding.GetType());
                dynamic b = Activator.CreateInstance(valueWriteAdding.GetType());
                
                var method = typeof(Slot).GetMethod("AttachComponent");
                var methodGeneric = method.MakeGenericMethod(valueWriteAdding);
                //var componentAdded = methodGeneric.Invoke(typeSlot);
            }
        }


        [HarmonyPatch(typeof(FrooxEngine.WebsocketClient), "OnChanges")]
        class WebsocketConnectPatch
        {
            static bool Prefix(System.Object __instance)
            {
                if (__instance.GetType() == typeof(FrooxEngine.WebsocketClient))
                {
                    FrooxEngine.WebsocketClient client = (FrooxEngine.WebsocketClient)__instance;
                    DynamicVariableSpace dynvarSpace;
                    string wasmUuid;
                    if (is_wasm_websocket(client, out dynvarSpace, out wasmUuid))
                    {
                        if (wasmUuid == null || wasmUuid.Length == 0)
                        { 
                            StaticBinary wasmFile;
                            FileMetadata wasmMetadata;
                            if (dynvarSpace.TryReadValue<FileMetadata>(WASM_METADATA, out wasmMetadata) && dynvarSpace.TryReadValue<StaticBinary>(WASM_FILE, out wasmFile)
                                && wasmFile != null && wasmMetadata != null)
                            {
                                Uri uri = wasmFile.URL.Value;
                                if (!(uri == null))
                                {
                                    Msg("Reading file");
                                    wasmMetadata.StartTask(async delegate
                                    {
                                        Msg("Reading file from uri" + uri);
                                        await default(ToBackground);
                                        string filePath = await wasmMetadata.Engine.AssetManager.GatherAssetFile(uri, 100.0f);
                                        if (filePath != null)
                                        {
                                            Msg("Read file into tmp " + filePath);
                                            Wasmtime.Module module = null;
                                            if (wasmMetadata.Filename.Value.ToLower().EndsWith(".wat"))
                                            {
                                                Msg("Reading wasm from text");
                                                module = Wasmtime.Module.FromTextFile(engine, filePath);
                                            }
                                            else if (wasmMetadata.Filename.Value.ToLower().EndsWith(".wasm"))
                                            {
                                                Msg("Reading wasm from binary");
                                                module = Wasmtime.Module.FromFile(engine, filePath);
                                            }
                                            if (module != null)
                                            {
                                                Msg("Read into wasm module!");
                                                await default(ToWorld);
                                                Guid wasm_guid = Guid.NewGuid();
                                                while (moduleLookup.ContainsKey(wasm_guid))
                                                {
                                                    wasm_guid = Guid.NewGuid();
                                                }
                                                moduleLookup[wasm_guid] = module;

                                                Slot exportTemplate;
                                                Slot importTemplate;
                                                if (dynvarSpace.TryReadValue<Slot>(WASM_IMPORT_TEMPLATE, out importTemplate) &&
                                                dynvarSpace.TryReadValue<Slot>(WASM_EXPORT_TEMPLATE, out exportTemplate))
                                                {
                                                    //addValueStuff(dynvarSpace.Slot, exportTemplate);
                                                    Slot imports = importTemplate.Duplicate();
                                                    imports.RemoveAllComponents(x => true);
                                                    imports.NameField.Value = "Imports";
                                                    imports.SetParent(dynvarSpace.Slot);

                                                    Slot functionImports = importTemplate.Duplicate();
                                                    Slot globalsImports = importTemplate.Duplicate();
                                                    Slot memoryImports = importTemplate.Duplicate();
                                                    Slot tableImports = importTemplate.Duplicate();
                                                    functionImports.RemoveAllComponents(x => true);
                                                    globalsImports.RemoveAllComponents(x => true);
                                                    memoryImports.RemoveAllComponents(x => true);
                                                    tableImports.RemoveAllComponents(x => true);
                                                    functionImports.SetParent(imports);
                                                    globalsImports.SetParent(imports);
                                                    memoryImports.SetParent(imports);
                                                    tableImports.SetParent(imports);
                                                    functionImports.NameField.Value = "Functions";
                                                    globalsImports.NameField.Value = "Globals";
                                                    memoryImports.NameField.Value = "Memory";
                                                    tableImports.NameField.Value = "Table";

                                                    foreach (Wasmtime.Import import in module.Imports)
                                                    {
                                                        Slot importDup = importTemplate.Duplicate();
                                                        DynamicVariableSpace importDynvarSpace = importDup.GetComponent<DynamicVariableSpace>();
                                                        if (importDynvarSpace != null)
                                                        {
                                                            importDynvarSpace.TryWriteValue<string>(WASM_IMPORT_NAME, import.Name);
                                                            importDup.Name = import.Name;

                                                            DynamicValueVariable<string> moduleNameVar = importDup.AttachComponent<DynamicValueVariable<string>>();
                                                            moduleNameVar.VariableName.Value = WASM_IMPORT_SPACE + "/moduleName";
                                                            moduleNameVar.Value.Value = import.ModuleName;

                                                            if (import.GetType() == typeof(Wasmtime.FunctionImport))
                                                            {
                                                                importDup.SetParent(functionImports);
                                                                Wasmtime.FunctionImport functionImport = (Wasmtime.FunctionImport)import;
                                                                importDynvarSpace.TryWriteValue<string>(WASM_IMPORT_TYPE, "function");

                                                                DynamicValueVariable<int> numInputsVar = importDup.AttachComponent<DynamicValueVariable<int>>();
                                                                numInputsVar.VariableName.Value = WASM_IMPORT_SPACE + "/numInputs";
                                                                numInputsVar.Value.Value = functionImport.Parameters.Count;

                                                                for (int i = 0; i < functionImport.Parameters.Count; i++)
                                                                {
                                                                    AttachValueKindToSlot(functionImport.Parameters[i], importDup,
                                                                        WASM_IMPORT_SPACE + "/inputKind" + i,
                                                                        WASM_IMPORT_SPACE + "/inputKindIsValueType" + i);
                                                                }

                                                                DynamicValueVariable<int> numResultsVar = importDup.AttachComponent<DynamicValueVariable<int>>();
                                                                numResultsVar.VariableName.Value = WASM_IMPORT_SPACE + "/numResults";
                                                                numResultsVar.Value.Value = functionImport.Results.Count;

                                                                for (int i = 0; i < functionImport.Results.Count; i++)
                                                                {
                                                                    AttachValueKindToSlot(functionImport.Results[i], importDup,
                                                                        WASM_IMPORT_SPACE + "/resultKind" + i,
                                                                        WASM_IMPORT_SPACE + "/resultKindIsValueType" + i);
                                                                }
                                                            }
                                                            else if (import.GetType() == typeof(Wasmtime.GlobalImport))
                                                            {
                                                                importDup.SetParent(globalsImports);
                                                                Wasmtime.GlobalImport globalImport = (Wasmtime.GlobalImport)import;
                                                                importDynvarSpace.TryWriteValue<string>(WASM_IMPORT_TYPE, "global");
                                                                AttachValueKindToSlot(globalImport.Kind, importDup, WASM_IMPORT_SPACE + "/kind", WASM_IMPORT_SPACE + "/kindIsValueType");
                                                                DynamicValueVariable<bool> isMutableVar = importDup.AttachComponent<DynamicValueVariable<bool>>();
                                                                isMutableVar.VariableName.Value = WASM_IMPORT_SPACE + "/mutable";
                                                                isMutableVar.Value.Value = globalImport.Mutability == Mutability.Mutable;
                                                            }
                                                            else if (import.GetType() == typeof(Wasmtime.MemoryImport))
                                                            {
                                                                importDup.SetParent(memoryImports);
                                                                Wasmtime.MemoryImport memoryImport = (Wasmtime.MemoryImport)import;
                                                                
                                                                importDynvarSpace.TryWriteValue<string>(WASM_IMPORT_TYPE, "memory");
                                                                DynamicValueVariable<long> minVar = importDup.AttachComponent<DynamicValueVariable<long>>();
                                                                minVar.VariableName.Value = WASM_IMPORT_SPACE + "/min";
                                                                minVar.Value.Value = memoryImport.Minimum;
                                                                DynamicValueVariable<bool> hasMaxVar = importDup.AttachComponent<DynamicValueVariable<bool>>();
                                                                hasMaxVar.VariableName.Value = WASM_IMPORT_SPACE + "/hasMax";
                                                                hasMaxVar.Value.Value = memoryImport.Maximum.HasValue;
                                                                if (memoryImport.Maximum.HasValue)
                                                                {
                                                                    DynamicValueVariable<long> maxVar = importDup.AttachComponent<DynamicValueVariable<long>>();
                                                                    maxVar.VariableName.Value = WASM_IMPORT_SPACE + "/max";
                                                                    maxVar.Value.Value = memoryImport.Maximum.Value;
                                                                }
                                                                DynamicValueVariable<bool> is64BitVar = importDup.AttachComponent<DynamicValueVariable<bool>>();
                                                                is64BitVar.VariableName.Value = WASM_IMPORT_SPACE + "/is64Bit";
                                                                is64BitVar.Value.Value = memoryImport.Is64Bit;
                                                            }
                                                            else if (import.GetType() == typeof(Wasmtime.TableImport))
                                                            {
                                                                importDup.SetParent(tableImports);
                                                                Wasmtime.TableImport tableImport = (Wasmtime.TableImport)import;
                                                                importDynvarSpace.TryWriteValue<string>(WASM_IMPORT_TYPE, "table");
                                                                DynamicValueVariable<uint> minVar = importDup.AttachComponent<DynamicValueVariable<uint>>();
                                                                minVar.VariableName.Value = WASM_IMPORT_SPACE + "/min";
                                                                minVar.Value.Value = tableImport.Minimum;
                                                                DynamicValueVariable<uint> maxVar = importDup.AttachComponent<DynamicValueVariable<uint>>();
                                                                maxVar.VariableName.Value = WASM_IMPORT_SPACE + "/max";
                                                                maxVar.Value.Value = tableImport.Maximum;
                                                                AttachValueKindToSlot(tableImport.Kind, importDup, WASM_IMPORT_SPACE + "/kind", WASM_IMPORT_SPACE + "/kindIsValueType");
                                                            }
                                                        }
                                                    }





                                                    Slot exports = exportTemplate.Duplicate();
                                                    exports.RemoveAllComponents(x => true);
                                                    exports.NameField.Value = "Exports";
                                                    exports.SetParent(dynvarSpace.Slot);

                                                    Slot functionExports = exportTemplate.Duplicate();
                                                    Slot globalsExports = exportTemplate.Duplicate();
                                                    Slot memoryExports = exportTemplate.Duplicate();
                                                    Slot tableExports = exportTemplate.Duplicate();
                                                    functionExports.RemoveAllComponents(x => true);
                                                    globalsExports.RemoveAllComponents(x => true);
                                                    memoryExports.RemoveAllComponents(x => true);
                                                    tableExports.RemoveAllComponents(x => true);
                                                    functionExports.SetParent(exports);
                                                    globalsExports.SetParent(exports);
                                                    memoryExports.SetParent(exports);
                                                    tableExports.SetParent(exports);
                                                    functionExports.NameField.Value = "Functions";
                                                    globalsExports.NameField.Value = "Globals";
                                                    memoryExports.NameField.Value = "Memory";
                                                    tableExports.NameField.Value = "Table";

                                                    foreach (Wasmtime.Import import in module.Imports)
                                                    {

                                                    }

                                                    foreach (Wasmtime.Export export in module.Exports)
                                                    {
                                                        Slot exportDup = exportTemplate.Duplicate();
                                                        DynamicVariableSpace exportDynvarSpace = exportDup.GetComponent<DynamicVariableSpace>();
                                                        if (exportDynvarSpace != null)
                                                        {
                                                            exportDynvarSpace.TryWriteValue<string>(WASM_EXPORT_NAME, export.Name);
                                                            exportDup.Name = export.Name;
                                                            
                                                            if (export.GetType() == typeof(Wasmtime.FunctionExport))
                                                            {
                                                                exportDup.SetParent(functionExports);
                                                                Wasmtime.FunctionExport functionExport = (Wasmtime.FunctionExport) export;
                                                                exportDynvarSpace.TryWriteValue<string>(WASM_EXPORT_TYPE, "function");

                                                                DynamicValueVariable<int> numInputsVar = exportDup.AttachComponent<DynamicValueVariable<int>>();
                                                                numInputsVar.VariableName.Value = WASM_EXPORT_SPACE + "/numInputs";
                                                                numInputsVar.Value.Value = functionExport.Parameters.Count;
                                                                
                                                                for (int i = 0; i < functionExport.Parameters.Count; i++)
                                                                {
                                                                    AttachValueKindToSlot(functionExport.Parameters[i], exportDup,
                                                                        WASM_EXPORT_SPACE + "/inputKind" + i,
                                                                        WASM_EXPORT_SPACE + "/inputKindIsValueType" + i);
                                                                }

                                                                DynamicValueVariable<int> numResultsVar = exportDup.AttachComponent<DynamicValueVariable<int>>();
                                                                numResultsVar.VariableName.Value = WASM_EXPORT_SPACE + "/numResults";
                                                                numResultsVar.Value.Value = functionExport.Results.Count;

                                                                for (int i = 0; i < functionExport.Results.Count; i++)
                                                                {
                                                                    AttachValueKindToSlot(functionExport.Results[i], exportDup,
                                                                        WASM_EXPORT_SPACE + "/resultKind" + i,
                                                                        WASM_EXPORT_SPACE + "/resultKindIsValueType" + i);
                                                                }
                                                            }
                                                            else if (export.GetType() == typeof(Wasmtime.GlobalExport))
                                                            {
                                                                exportDup.SetParent(globalsExports);
                                                                Wasmtime.GlobalExport globalExport = (Wasmtime.GlobalExport)export;
                                                                exportDynvarSpace.TryWriteValue<string>(WASM_EXPORT_TYPE, "global");
                                                                AttachValueKindToSlot(globalExport.Kind, exportDup, WASM_EXPORT_SPACE + "/kind", WASM_EXPORT_SPACE + "/kindIsValueType");
                                                                DynamicValueVariable<bool> isMutableVar = exportDup.AttachComponent<DynamicValueVariable<bool>>();
                                                                isMutableVar.VariableName.Value = WASM_EXPORT_SPACE + "/mutable";
                                                                isMutableVar.Value.Value = globalExport.Mutability == Mutability.Mutable;
                                                            }
                                                            else if (export.GetType() == typeof(Wasmtime.MemoryExport))
                                                            {
                                                                exportDup.SetParent(memoryExports);
                                                                Wasmtime.MemoryExport memoryExport = (Wasmtime.MemoryExport)export;
                                                                
                                                                exportDynvarSpace.TryWriteValue<string>(WASM_EXPORT_TYPE, "memory");
                                                                DynamicValueVariable<long> minVar = exportDup.AttachComponent<DynamicValueVariable<long>>();
                                                                minVar.VariableName.Value = WASM_EXPORT_SPACE + "/min";
                                                                minVar.Value.Value = memoryExport.Minimum;
                                                                DynamicValueVariable<bool> hasMaxVar = exportDup.AttachComponent<DynamicValueVariable<bool>>();
                                                                hasMaxVar.VariableName.Value = WASM_EXPORT_SPACE + "/hasMax";
                                                                hasMaxVar.Value.Value = memoryExport.Maximum.HasValue;
                                                                if (memoryExport.Maximum.HasValue)
                                                                {
                                                                    DynamicValueVariable<long> maxVar = exportDup.AttachComponent<DynamicValueVariable<long>>();
                                                                    maxVar.VariableName.Value = WASM_EXPORT_SPACE + "/max";
                                                                    maxVar.Value.Value = memoryExport.Maximum.Value;
                                                                }
                                                                DynamicValueVariable<bool> is64BitVar = exportDup.AttachComponent<DynamicValueVariable<bool>>();
                                                                is64BitVar.VariableName.Value = WASM_EXPORT_SPACE + "/is64Bit";
                                                                is64BitVar.Value.Value = memoryExport.Is64Bit;
                                                            }
                                                            else if (export.GetType() == typeof(Wasmtime.TableExport))
                                                            {
                                                                exportDup.SetParent(tableExports);
                                                                Wasmtime.TableExport tableExport = (Wasmtime.TableExport)export;
                                                                exportDynvarSpace.TryWriteValue<string>(WASM_EXPORT_TYPE, "table");
                                                                DynamicValueVariable<uint> minVar = exportDup.AttachComponent<DynamicValueVariable<uint>>();
                                                                minVar.VariableName.Value = WASM_EXPORT_SPACE + "/min";
                                                                minVar.Value.Value = tableExport.Minimum;
                                                                DynamicValueVariable<uint> maxVar = exportDup.AttachComponent<DynamicValueVariable<uint>>();
                                                                maxVar.VariableName.Value = WASM_EXPORT_SPACE  + "/max";
                                                                maxVar.Value.Value = tableExport.Maximum;
                                                                AttachValueKindToSlot(tableExport.Kind, exportDup, WASM_EXPORT_SPACE + "/kind", WASM_EXPORT_SPACE + "/kindIsValueType");
                                                            }
                                                        }
                                                    }
                                                }
                                                
                                                Msg("trying write guid " + wasm_guid.ToString());
                                                dynvarSpace.TryWriteValue<string>(WASM_UUID, wasm_guid.ToString());
                                            }
                                        }
                                    });

                                    return false;
                                }
                            }
                        }
                    }
                }
                return true;
            }
        }
    }
}