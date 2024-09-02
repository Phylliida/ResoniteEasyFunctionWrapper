using HarmonyLib; // HarmonyLib comes included with a ResoniteModLoader install
using ResoniteModLoader;
using System;
using System.Reflection;
using ProtoFlux.Core;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using System.Collections.Generic;
using FrooxEngine.ProtoFlux;
using ProtoFlux.Runtimes.Execution;
using System.Threading.Tasks;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Network;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Variables;
using ResoniteHotReloadLib;
using System.Xml.XPath;
using System.CodeDom;
using static ResoniteEasyFunctionWrapper.ResoniteEasyFunctionWrapper;

namespace ResoniteEasyFunctionWrapper
{
    public class ResoniteEasyFunctionWrapper : ResoniteMod
    {
        static string RESONITE_WRAPPER_PATH = "Generate Wrapper Flux";

        static string MOD_PREFIX = "mod://";

        public enum VariableKind
        {
            Parameter,
            TupleFromReturn,
            FromAsyncTaskReturn,
            ReturnValue
        }
        public struct VariableInfo
        {
            public Type type;
            public string name;
            public VariableKind variableKind;
            public int paramIndex;
            public bool isValidResoniteType;
            public bool isResoniteTypeValueType;
            public Type resoniteType;

            static bool isTypeValidResoniteType(Type type, out bool isValueType)
            {
                try
                {
                    bool result = typeof(DynamicValueVariable<>).MakeGenericType(type).IsValidGenericType(validForInstantiation: true) ||
                     typeof(DynamicReferenceVariable<>).MakeGenericType(type).IsValidGenericType(validForInstantiation: true);
                    
                    // DynamicValueVariables are an exception where string is a value instead of object
                    isValueType = typeof(DynamicValueVariable<>).MakeGenericType(type).IsValidGenericType(validForInstantiation: true) && type != typeof(string);
                    return result;

                }
                catch (ArgumentException) // happens if invalid type
                {
                    isValueType = false;
                    return false;
                }
            }

            public VariableInfo(string name, Type type, VariableKind variableKind, int paramIndex = 0)
            {
                this.name = name;
                this.type = type;
                this.variableKind = variableKind;
                this.paramIndex = paramIndex;
                this.isValidResoniteType = isTypeValidResoniteType(type, out this.isResoniteTypeValueType);
                this.resoniteType = this.isValidResoniteType ? type : typeof(string);
            }

            public override string ToString()
            {
                return "{Variable Info: name:" + name
                    + " type:" + type
                    + " variableKind:" + variableKind
                    + " paramIndex:" + paramIndex
                    + " isValidResoniteType:" + isValidResoniteType
                    + " resoniteType:" + resoniteType;
            }
        }

        public class RollingCache
        {
            public int offset;
            public object[] values;
            public Guid[] keys;
            public RollingCache(int capacity)
            {
                values = new object[capacity];
                keys = new Guid[capacity];
            }

            public bool TryLookup(object value, out Guid guid)
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

            public void Add(object value, Guid key)
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

            public Guid Add(object value)
            {
                if (value == null)
                {
                    return Guid.Empty;
                }
                return GetHelperForType(value.GetType()).Add(value);
            }

            public bool TryGet(Guid guid, Type type, out object value)
            {
                if (guid == Guid.Empty)
                {
                    value = null;
                    return true;
                }
                return GetHelperForType(type).TryGet(guid, out value);
            }
        }

        public class UnsupportedTypeLookupHelper
        {
            RollingCache cache;
            public Dictionary<Guid, object> lookup = new Dictionary<Guid, object>();
            public UnsupportedTypeLookupHelper(int cacheCapacity)
            {
                cache = new RollingCache(cacheCapacity);
            }

            public Guid Add(object value)
            {
                Guid guid;
                // we have a cache so the first 10 or so values won't allocate new dict entries every call
                if (!cache.TryLookup(value, out guid))
                {
                    guid = Guid.NewGuid();
                    while (lookup.ContainsKey(guid) || guid == Guid.Empty)
                    {
                        guid = Guid.NewGuid();
                    }
                    lookup[guid] = value;
                    cache.Add(value, guid);
                }
                return guid;
            }

            public bool TryGet(Guid guid, out object value)
            {
                if (guid == Guid.Empty) // empty guid is null guid
                {
                    value = null;
                    return true;
                }
                return lookup.TryGetValue(guid, out value);
            }
        }

        public static void GetMethodVars(MethodInfo method, bool isAsync, out List<VariableInfo> inputVars, out List<VariableInfo> returnVars)
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
                    for (var i = 0; i < genType.GetGenericArguments().Length; i++)
                    {
                        returnVars.Add(new VariableInfo(i.ToString(), genType.GetGenericArguments()[i], VariableKind.TupleFromReturn, i));
                    }
                }
                else if(genType == typeof(Task<>) && isAsync)
                {
                    Msg("Got task with generic type " + returnType.GetGenericArguments()[0]);
                    returnVars.Add(new VariableInfo("0", returnType.GetGenericArguments()[0], VariableKind.FromAsyncTaskReturn, 0));
                }
                else
                {
                    returnVars.Add(new VariableInfo("0", returnType, VariableKind.ReturnValue));
                }
            }
            else if(returnType != typeof(void)) // don't use void as a type
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
                    // GetElementType is needed for byref types otherwise they have a & at the end and it confuses things
                    returnVars.Add(new VariableInfo(param.Name, param.ParameterType.GetElementType(), VariableKind.Parameter, paramIndex: i));
                }
                // ref type, its an input and output
                else if (param.ParameterType.IsByRef && !param.IsOut)
                {
                    inputVars.Add(new VariableInfo(param.Name, param.ParameterType.GetElementType(), VariableKind.Parameter, paramIndex: i));
                    returnVars.Add(new VariableInfo(param.Name, param.ParameterType.GetElementType(), VariableKind.Parameter, paramIndex: i));
                }
                // input type
                else if (!param.IsOut)
                {
                    inputVars.Add(new VariableInfo(param.Name, param.ParameterType, VariableKind.Parameter, paramIndex: i));
                }
            }
        }
        public class WrappedMethod : IDisposable
        {
            public MethodInfo method;
            public bool isAsync;
            public string name;
            public string modNamespace;
            List<VariableInfo> inputVars;
            List<VariableInfo> outputVars;
            public WrappedMethod(MethodInfo method, string modNamespace)
            {
                this.method = method;
                this.name = method.Name;
                this.modNamespace = modNamespace;
                this.isAsync = IsAsyncMethod(method);
                GetMethodVars(method, this.isAsync, out inputVars, out outputVars);
                AddReloadMenuOption();
            }


            // From https://stackoverflow.com/a/20350646, I decided just returning Task should not be counted as async so functions can pass around task if they want
            private static bool IsAsyncMethod(MethodInfo method)
            {
                Type attType = typeof(AsyncStateMachineAttribute);

                // Obtain the custom attribute for the method. 
                // The value returned contains the StateMachineType property. 
                // Null is returned if the attribute isn't present for the method. 
                var attrib = (AsyncStateMachineAttribute)method.GetCustomAttribute(attType);

                return (attrib != null);
            }

            public void Dispose()
            {
                RemoveMenuOption();
            }

            class MethodCallException : Exception
            {
                public string message;
                public MethodCallException(string message)
                {
                    this.message = message;
                }

            }

            public static object ReadField(object obj, string fieldName)
            {
                return obj.GetType().GetField(fieldName).GetValue(obj);
            }

            public static object ReadProperty(object obj, string propertyName)
            {
                return obj.GetType().GetProperty(propertyName).GetValue(obj);
            }

            public static void SetField(object obj, string fieldName, object value)
            {
                obj.GetType().GetField(fieldName).SetValue(obj, value);
            }

            public static void SetProperty(object obj, string propertyName, object value)
            {
                obj.GetType().GetProperty(propertyName).SetValue(obj, value);
            }

            public object[] GetParameters(DynamicVariableSpace space, UnsupportedTypeLookup typeLookup)
            {
                object[] parameters = new object[method.GetParameters().Length];
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
                    if (!found)
                    {
                        throw new MethodCallException("In Dynvar, could not find parameter " + inputVar);
                    }
                    if (inputVar.isValidResoniteType)
                    {
                        parameters[inputVar.paramIndex] = value;
                    }
                    else
                    {
                        // if not a valid resonite type we need to use our lookup table to find the value
                        // because we just store a string with a guid pointing to it
                        object nonResoniteValue;
                        Guid inputParamGuid;
                        if (Guid.TryParse((string)value, out inputParamGuid) &&
                            typeLookup.TryGet(inputParamGuid, inputVar.type, out nonResoniteValue))
                        {
                            parameters[inputVar.paramIndex] = nonResoniteValue;
                        }
                        else
                        {
                            throw new MethodCallException("Failed to lookup object parameter " + inputVar + " with uuid " + value);
                        }
                    }
                }
                return parameters;
            }

            public void WriteResult(object result, object[] parameters, DynamicVariableSpace space, UnsupportedTypeLookup typeLookup)
            {
                object[] dummyParams = new object[2] { null, null };
                foreach (VariableInfo returnVar in outputVars)
                {
                    object value = null;

                    switch (returnVar.variableKind)
                    {
                        case VariableKind.Parameter:
                            value = parameters[returnVar.paramIndex];
                            break;
                        case VariableKind.ReturnValue:
                            value = result;
                            break;
                        case VariableKind.FromAsyncTaskReturn:
                            value = result;
                            break;
                        case VariableKind.TupleFromReturn:
                            ITuple resultTuple = result as ITuple;
                            if (result == null || resultTuple == null)
                            {
                                throw new MethodCallException("Expected tuple, returned null");
                            }
                            value = resultTuple[returnVar.paramIndex];
                            break;
                    }
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
                    DynamicVariableWriteResult writeResult = (DynamicVariableWriteResult)genericWriteMethod.Invoke(space, dummyParams);

                    switch (writeResult)
                    {
                        case DynamicVariableWriteResult.Success:
                            break;
                        case DynamicVariableWriteResult.NotFound:
                            throw new MethodCallException("Could not find dynvar for output variagble " + returnVar);
                        case DynamicVariableWriteResult.Failed:
                            throw new MethodCallException("Failed to write dynvar for output variagble " + returnVar);
                    }
                }
            }

            public void CallMethod(Slot dataSlot, UnsupportedTypeLookup typeLookup)
            {
                DynamicVariableSpace space = dataSlot.GetComponent<DynamicVariableSpace>();
                if (space == null)
                {
                    return;
                }
                try
                {
                    object[] parameters = GetParameters(space: space, typeLookup: typeLookup);
                    // null for first input to Invoke means static, we only support static methods
                    var result = this.method.Invoke(null, parameters);
                    WriteResult(result, parameters, space, typeLookup);

                }
                catch (MethodCallException methodCallException)
                {
                    Msg("Writing error " + methodCallException.message);
                    space.TryWriteValue("error", methodCallException.message);
                }
            }

            public async Task CallMethodAsync(Slot dataSlot, UnsupportedTypeLookup typeLookup)
            {
                DynamicVariableSpace space = dataSlot.GetComponent<DynamicVariableSpace>();
                if (space == null)
                {
                    return;
                }
                try
                {
                    object[] parameters = GetParameters(space: space, typeLookup: typeLookup);
                    // null for first input to Invoke means static, we only support static methods
                    var task = (Task)(this.method.Invoke(null, parameters));
                    await task;
                    object result = null;
                    // if the Task has a type (and isn't just Task, no return value)
                    if (this.method.ReturnType.IsGenericType
                       && task.GetType().GetProperty("Result") != null)
                    {
                        result = ReadProperty(task, "Result");
                    }
                    WriteResult(result, parameters, space, typeLookup);
                }
                catch (MethodCallException methodCallException)
                {
                    Msg("Writing error " + methodCallException.message);
                    space.TryWriteValue("error", methodCallException.message);
                }
            }


            void CreateVarsForVar(Slot slot, string spaceName, VariableInfo var)
            {
                Type dynvarType = (var.isResoniteTypeValueType || var.resoniteType == typeof(System.String))
                    ? typeof(DynamicValueVariable<>).MakeGenericType(var.resoniteType)
                    : typeof(DynamicReferenceVariable<>).MakeGenericType(var.resoniteType);

                var attached = slot.AttachComponent(dynvarType);
                Sync<string> variableName = (Sync<string>)ReadField(attached, "VariableName");
                variableName.Value = spaceName + "/" + var.name;
            }

            public Uri GetUri()
            {
                return new Uri(MOD_PREFIX + modNamespace + "/" + method.Name);
            }

            public Slot CreateTemplate(Slot holder)
            {
                Slot template = holder.AddSlot("ParameterTemplate");
                template.OrderOffset = 99999; // important so custom nodes work properly
                DynamicVariableSpace space = template.AttachComponent<DynamicVariableSpace>();
                space.SpaceName.Value = method.Name;
                space.OnlyDirectBinding.Value = true;
                foreach (VariableInfo varInfo in inputVars)
                {
                    CreateVarsForVar(template, method.Name, varInfo);
                }
                foreach (VariableInfo varInfo in outputVars)
                {
                    CreateVarsForVar(template, method.Name, varInfo);
                }
                WebsocketClient client = template.AttachComponent<WebsocketClient>();
                client.URL.Value = GetUri();

                DynamicReferenceVariable<WebsocketClient> wsVar = template.AttachComponent<DynamicReferenceVariable<WebsocketClient>>();
                wsVar.VariableName.Value = method.Name + "/" + "FAKE_WS_CLIENT";
                wsVar.Reference.Value = client.ReferenceID;

                DynamicValueVariable<string> errorMessage = template.AttachComponent<DynamicValueVariable<string>>();
                errorMessage.VariableName.Value = method.Name + "/" + "error";
                errorMessage.Value.Value = "";

                return template;
            }

            public T CreateSlotWithComponent<T>(Slot parent, string name, float3 pos, Slot addToSlot) where T : Component
            {
                return (T)CreateSlotWithComponent(parent: parent, componentType: typeof(T), name: name, pos: pos, addToSlot: addToSlot);
            }

            public object CreateSlotWithComponent(Slot parent, Type componentType, string name, float3 pos, Slot addToSlot)
            {
                if (addToSlot == null)
                {
                    addToSlot = parent.AddSlot(name);
                    addToSlot.Position_Field.Value = pos;
                }
                return addToSlot.AttachComponent(componentType);
            }

            public Slot CreateFlux(bool monopack)
            {
                Slot addToSlot = null;
                Slot holder = FrooxEngine.Engine.Current.WorldManager.FocusedWorld.LocalUserSpace.AddSlot(method.Name);
                Slot template = CreateTemplate(holder);

                if (monopack)
                {
                    addToSlot = holder.AddSlot("Monopacked flux");
                }
                AsyncCallRelay relay = CreateSlotWithComponent<AsyncCallRelay>(
                    holder, 
                    "AsyncCallRelay:Call", 
                    new float3(-0.5f, 0.19f, 0), 
                    null);
                
                RefObjectInput<Slot> templateInput = CreateSlotWithComponent<RefObjectInput<Slot>>(
                    holder,
                    "RefObjectInput`1",
                    new float3(-0.28f, 0.37f, 0.02f),
                    addToSlot);
                templateInput.Target.Value = template.ReferenceID;

                ReadDynamicObjectVariable<WebsocketClient> wsClientVar = CreateSlotWithComponent<ReadDynamicObjectVariable<WebsocketClient>>(
                    holder,
                    "ReadDynamicObjectVariable`1",
                    new float3(0, 0.3f, 0),
                    addToSlot);
                ValueObjectInput<string> wsClientId = CreateSlotWithComponent<ValueObjectInput<string>>(
                    holder,
                    "ValueObjectInput`1",
                    new float3(-0.223f, 0.29f, 0),
                    addToSlot);
                wsClientId.Value.Value = method.Name + "/" + "FAKE_WS_CLIENT";
                wsClientVar.Source.Value = templateInput.ReferenceID;
                wsClientVar.Path.Value = wsClientId.ReferenceID;

                WebsocketTextMessageSender sender = CreateSlotWithComponent<WebsocketTextMessageSender>(
                    holder,
                    "WebsocketTextMessageSender",
                    new float3(0.2f, 0.28f, 0),
                    addToSlot);
                sender.Client.Value = wsClientVar.Value.ReferenceID;

                SyncRef<INodeOperation> curNode = relay.OnTriggered;

                float3 offset = new float3(0, -0.17f, 0);
                float3 curOffset = wsClientVar.Slot.LocalPosition;
                foreach (VariableInfo inputVar in inputVars)
                {
                    curOffset += offset;
                    Type writeDynvarType = inputVar.isResoniteTypeValueType
                        ? typeof(WriteDynamicValueVariable<>).MakeGenericType(inputVar.resoniteType)
                        : typeof(WriteDynamicObjectVariable<>).MakeGenericType(inputVar.resoniteType);
                    string writeName = inputVar.isResoniteTypeValueType
                        ? "WriteDynamicValueVariable`1"
                        : "WriteDynamicObjectVariable`1";
                    var attachedInputVarWriter = CreateSlotWithComponent(
                        parent: holder,
                        componentType:writeDynvarType,
                        name: writeName,
                        pos: curOffset,
                        addToSlot: addToSlot);
                    // Write to Target (uses field holding template)
                    var targetField = ReadField(attachedInputVarWriter, "Target");
                    SetProperty(targetField, "Value", templateInput.ReferenceID);

                    // Create Path field and write to Path
                    ValueObjectInput<string> inputVarId = CreateSlotWithComponent<ValueObjectInput<string>>(
                        holder,
                        "ValueObjectInput`1",
                        curOffset + new float3(-0.223f, -0.01f, -0.01f),
                        addToSlot);
                    inputVarId.Value.Value = method.Name + "/" + inputVar.name;
                    var pathField = ReadField(attachedInputVarWriter, "Path");
                    SetProperty(pathField, "Value", inputVarId.ReferenceID);

                    // Create Relay
                    Type relayType = inputVar.isResoniteTypeValueType
                        ? typeof(ValueRelay<>).MakeGenericType(inputVar.resoniteType)
                        : typeof(ObjectRelay<>).MakeGenericType(inputVar.resoniteType);
                    var relayComponent = CreateSlotWithComponent(
                        parent: holder,
                        componentType: relayType,
                        name: "Relay:" + inputVar.name,
                        pos: curOffset + new float3(-0.5f, -0.07f, 0),
                        addToSlot: addToSlot);
                    var relayRefId = ReadProperty(relayComponent, "ReferenceID");

                    // Write relay to Value
                    var valueField = ReadField(attachedInputVarWriter, "Value");
                    SetProperty(valueField, "Value", relayRefId);

                    curNode.Value = (RefID)ReadProperty(attachedInputVarWriter, "ReferenceID");
                    curNode = (SyncRef<INodeOperation>)ReadField(attachedInputVarWriter, "OnSuccess");
                }

                curNode.Value = sender.ReferenceID;
                curNode = sender.OnSent;

                offset = new float3(0, -0.17f, 0);
                curOffset = wsClientVar.Slot.LocalPosition + new float3(0.5f, 0, 0);
                foreach (VariableInfo outputVar in outputVars)
                {
                    curOffset += offset;

                    Type readDynvarType = outputVar.isResoniteTypeValueType
                        ? typeof(ReadDynamicValueVariable<>).MakeGenericType(outputVar.resoniteType)
                        : typeof(ReadDynamicObjectVariable<>).MakeGenericType(outputVar.resoniteType);
                    string readName = outputVar.isResoniteTypeValueType
                        ? "ReadDynamicValueVariable`1"
                        : "ReadDynamicObjectVariable`1";
                    var attachedReturnVarReader = CreateSlotWithComponent(
                        parent: holder,
                        componentType: readDynvarType,
                        name: readName,
                        pos: curOffset + new float3(0, 0.0025f, 0),
                        addToSlot: addToSlot);

                    // Write to Source (uses field holding template)
                    var sourceField = ReadField(attachedReturnVarReader, "Source");
                    SetProperty(sourceField, "Value", templateInput.ReferenceID);

                    // Create Path field and write to Path
                    ValueObjectInput<string> returnVarId = CreateSlotWithComponent<ValueObjectInput<string>>(
                        holder,
                        "ValueObjectInput`1",
                        curOffset + new float3(-0.223f, -0.01f, -0.01f),
                        addToSlot);
                    returnVarId.Value.Value = method.Name + "/" + outputVar.name;
                    var pathField = ReadField(attachedReturnVarReader, "Path");
                    SetProperty(pathField, "Value", returnVarId.ReferenceID);

                    // Create Write
                    Type writeType = outputVar.isResoniteTypeValueType ?
                        typeof(ValueWrite<>).MakeGenericType(outputVar.resoniteType) :
                        typeof(ObjectWrite<>).MakeGenericType(outputVar.resoniteType);
                    string writeName = (outputVar.isResoniteTypeValueType ?
                        "Value" : "Object") + "Write`1";

                    var writeComponent = CreateSlotWithComponent(
                        parent: holder,
                        componentType: writeType,
                        name: writeName,
                        pos: curOffset + new float3(0.15f, -0.005f, 0),
                        addToSlot: addToSlot);

                    // Attach DynvarValue -> Write Value
                    var writeValueField = ReadField(writeComponent, "Value");
                    var dynvarReadValueRefIdField = ReadField(attachedReturnVarReader, "Value");
                    var dynvarReadValueRefId = (RefID)ReadProperty(dynvarReadValueRefIdField, "ReferenceID");
                    SetProperty(writeValueField, "Value", dynvarReadValueRefId);

                    curNode.Value = (RefID)ReadProperty(writeComponent, "ReferenceID");
                    curNode = (SyncRef<INodeOperation>)ReadField(writeComponent, "OnWritten");

                    // Create local
                    Type localType = outputVar.isResoniteTypeValueType ?
                        typeof(LocalValue<>).MakeGenericType(outputVar.resoniteType) :
                        typeof(LocalObject<>).MakeGenericType(outputVar.resoniteType);

                    var localComponent = CreateSlotWithComponent(
                        parent: holder,
                        componentType: localType,
                        name: outputVar.name,
                        pos: curOffset + new float3(0.35f, 0, -0.01f),
                        addToSlot: addToSlot);
                    
                    var localComponentRefId = ReadProperty(localComponent, "ReferenceID");
                    var writeVarField = ReadField(writeComponent, "Variable");
                    SetProperty(writeVarField, "Value", localComponentRefId);
                    
                    // Create Relay
                    Type relayType = outputVar.isResoniteTypeValueType ?
                        typeof(ValueRelay<>).MakeGenericType(outputVar.resoniteType) :
                        typeof(ObjectRelay<>).MakeGenericType(outputVar.resoniteType);

                    var relayComponent = CreateSlotWithComponent(
                        parent: holder,
                        componentType: relayType,
                        name: "Relay:" + outputVar.name,
                        pos: curOffset + new float3(0.5f, 0, 0),
                        addToSlot: null); // relays don't get monopacked

                    // Connect Local Value To Relay
                    var relayInputField = ReadField(relayComponent, "Input");
                    SetProperty(relayInputField, "Value", localComponentRefId);
                }

                AsyncCallRelay outrelay = CreateSlotWithComponent<AsyncCallRelay>(
                    holder,
                    "AsyncCallRelay:OnDone",
                    new float3(1f, 0.3f, 0),
                    null);

                curNode.Value = outrelay.ReferenceID;

                Slot headSlot = FrooxEngine.Engine.Current.WorldManager.FocusedWorld.LocalUser.GetBodyNodeSlot(BodyNode.Head);
                if (headSlot != null)
                {
                    holder.GlobalPosition = headSlot.GlobalPosition + new float3(headSlot.Forward)*0.2f;
                    holder.GlobalRotation = headSlot.GlobalRotation;
                    // face the user, locally, but only on y axis
                    holder.LocalRotation = floatQ.Euler(0, holder.LocalRotation.EulerAngles.y, 0);
                }

                if (!monopack)
                {
                    holder.UnpackNodes();
                }

                return holder;
            }

            public string GetLabelString()
            {
                return modNamespace + "/" + name;
            }

            public void AddReloadMenuOption()
            {
                // modified from https://github.com/Nytra/ResoniteHotReloadLib/blob/11bc8c4167387d75fda0eed07237fee5424cb33c/HotReloader.cs#L344
                Msg("Begin Add Generate Flux Menu Option, for " + modNamespace + " for function " + name);
                if (!FrooxEngine.Engine.Current.IsInitialized)
                {
                    FrooxEngine.Engine.Current.RunPostInit(AddActionDelegate);
                }
                else
                {
                    AddActionDelegate();
                }
                void AddActionDelegate()
                {
                    DevCreateNewForm.AddAction(RESONITE_WRAPPER_PATH, GetLabelString(), (x) =>
                    {
                        x.Destroy();

                        Slot result = CreateFlux(false);
                    });
                }
            }

            public bool RemoveMenuOption()
            {
                object categoryNode = AccessTools.Field(typeof(DevCreateNewForm), "root").GetValue(null);
                object subcategory = AccessTools.Method(categoryNode.GetType(), "GetSubcategory").Invoke(categoryNode, new object[] { RESONITE_WRAPPER_PATH });
                System.Collections.IList elements = (System.Collections.IList)AccessTools.Field(categoryNode.GetType(), "_elements").GetValue(subcategory);
                if (elements == null)
                {
                    Msg("Elements is null!");
                    return false;
                }
                foreach (object categoryItem in elements)
                {
                    var name = (string)AccessTools.Field(categoryNode.GetType().GetGenericArguments()[0], "name").GetValue(categoryItem);
                    if (name == GetLabelString())
                    {
                        elements.Remove(categoryItem);
                        Msg("Menu option removed for " + GetLabelString());
                        return true;
                    }
                }
                return false;
            }
        }

        public static int CACHE_CAPACITY = 20;

        public static UnsupportedTypeLookup globalTypeLookup = new UnsupportedTypeLookup(CACHE_CAPACITY);

        public static Dictionary<Uri, WrappedMethod> wrappedMethods = new Dictionary<Uri, WrappedMethod>();

        public static Dictionary<Tuple<string, string>, List<Uri>> wrappedMethodLookup = new Dictionary<Tuple<string, string>, List<Uri>>();

        static WrappedMethod WrapMethodInternal(MethodInfo method, string modNamespace)
        {
            WrappedMethod wrappedMethod = new WrappedMethod(method, modNamespace + "/" + method.Name);
            wrappedMethods[wrappedMethod.GetUri()] = wrappedMethod;
            return wrappedMethod;
        }

        public static void WrapMethod(MethodInfo method, string modNamespace)
        {
            if (method.IsStatic && method.IsPublic)
            {
                WrappedMethod wrappedMethod = WrapMethodInternal(method: method, modNamespace: modNamespace);
                List<Uri> methodUris = new List<Uri>();
                methodUris.Add(wrappedMethod.GetUri());
                wrappedMethodLookup[new Tuple<string, string>(MethodIdentifier(method), modNamespace)] = methodUris;
            }
            else
            {
                throw new ArgumentException("Method " + method.Name + " of type " + method.GetType() + " is not public and static, cannot wrap");
            }
        }

        public static void UnwrapMethod(MethodInfo method, string modNamespace)
        {
            Tuple<string, string> key = new Tuple<string, string>(MethodIdentifier(method), modNamespace);
            if (wrappedMethodLookup.ContainsKey(key))
            {
                List<Uri> uris = wrappedMethodLookup[key];
                UnwrapUris(uris);
                wrappedMethodLookup.Remove(key);
            }
            else
            {
                Warn("Tried to clean up method " + method.Name + " with type " + method.GetType().ToString() + " and namespace " + modNamespace + " but did not exist, did you create it?");
            }
        }

        static string MethodIdentifier(MethodInfo method)
        {
            return method.Name + ":" + method.GetType().ToString();
        }

        public static void WrapClass(Type classType, string modNamespace)
        {
            List<Uri> methodUris = new List<Uri>();
            foreach (MethodInfo method in classType.GetMethods())
            {
                if (method.IsStatic && method.IsPublic)
                {
                    WrappedMethod wrappedMethod = WrapMethodInternal(method: method, modNamespace: modNamespace);
                    methodUris.Add(wrappedMethod.GetUri());
                }
                else
                {
                    Msg("Not wrapping method " + method.Name + " in namespace " + modNamespace + " because it is not public and static");
                }
            }
            wrappedMethodLookup[new Tuple<string, string>(classType.ToString(), modNamespace)] = methodUris;
        }

        public static void UnwrapClass(Type classType, string modNamespace)
        {
            Tuple<string, string> key = new Tuple<string, string>(classType.ToString(), modNamespace);
            if (wrappedMethodLookup.ContainsKey(key))
            {
                List<Uri> uris = wrappedMethodLookup[key];
                UnwrapUris(uris);
                wrappedMethodLookup.Remove(key);
            }
            else
            {
                Warn("Tried to clean up class with type " + classType.ToString() + " and namespace " + modNamespace + " but did not exist, did you create it?");
            }
        }

        static void UnwrapUris(List<Uri> uris)
        {
            foreach (Uri uri in uris)
            {
                if (wrappedMethods.ContainsKey(uri))
                {
                    WrappedMethod wrappedMethod = wrappedMethods[uri];
                    if (wrappedMethod != null)
                    {
                        wrappedMethod.Dispose();
                    }
                    else
                    {
                        Warn("Wrapped method is null?? with uri " + uri);
                    }
                    wrappedMethods.Remove(uri);
                }
            }
        }
        public override string Name => "ResoniteEasyFunctionWrapper";
        public override string Author => "TessaCoil";
        public override string Version => "1.0.3"; //Version of the mod, should match the AssemblyVersion
        public override string Link => "https://github.com/Phylliida/ResoniteWrapper"; // Optional link to a repo where this mod would be located

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> enabled = new ModConfigurationKey<bool>("enabled", "Should the mod be enabled", () => true); //Optional config settings

        private static ModConfiguration Config; //If you use config settings, this will be where you interface with them.
        private static string harmony_id = "bepis.Phylliida.ResoniteEasyFunctionWrapper";

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
            harmony = new Harmony(harmony_id);
            harmony.PatchAll();
        }

        static void CleanupAllWrappedFunctions()
        {
            foreach (KeyValuePair<Uri, WrappedMethod> wrapped in wrappedMethods)
            {
                wrapped.Value.Dispose();
            }
            wrappedMethods.Clear();
            wrappedMethodLookup.Clear();
        }

        static void BeforeHotReload()
        {
            harmony = new Harmony(harmony_id);
            // This runs in the current assembly (i.e. the assembly which invokes the Hot Reload)
            harmony.UnpatchAll();
            
            // This is where you unload your mod, free up memory, and remove Harmony patches etc.
            CleanupAllWrappedFunctions();
        }

        static void OnHotReload(ResoniteMod modInstance)
        {
            // This runs in the new assembly (i.e. the one which was loaded fresh for the Hot Reload)
            
            // Get the config
            Config = modInstance.GetConfiguration();

            // Now you can setup your mod again
            SetupMod();
        }

        [HarmonyPatch(typeof(ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Network.WebsocketTextMessageSender), "RunAsync")]
        class WebsocketSendPatch
        {
            public static async Task<IOperation> CallAsync(FrooxEngineContext context, WrappedMethod wrappedMethod, WebsocketClient websocketClient, ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Network.WebsocketTextMessageSender __instance)
            {
                await wrappedMethod.CallMethodAsync(websocketClient.Slot, globalTypeLookup);
                return __instance.OnSent.Target;
            }

            [HarmonyPrefix]
            static bool Prefix(ProtoFlux.Runtimes.Execution.Nodes.FrooxEngine.Network.WebsocketTextMessageSender __instance, FrooxEngineContext context, ref Tuple<FrooxEngineContext, Task> __state, ref Task<IOperation> __result)
            {
                __state = null;
                WebsocketClient websocketClient = __instance.Client.Evaluate(context);
                if (websocketClient != null && websocketClient.URL.Value != null)
                {
                    if (websocketClient.URL.Value.ToString().StartsWith(MOD_PREFIX))
                    {
                        if (wrappedMethods.ContainsKey(websocketClient.URL))
                        {
                            WrappedMethod wrappedMethod = wrappedMethods[websocketClient.URL];
                            if (wrappedMethod.isAsync)
                            {
                                __result = CallAsync(context, wrappedMethod, websocketClient, __instance);
                            }
                            else
                            {
                                wrappedMethod.CallMethod(websocketClient.Slot, globalTypeLookup);
                                __result = Task.FromResult<IOperation>(__instance.OnSent.Target);
                            }
                            return false;
                        }
                    }
                }                
                return true;
            }
        }
    }
}
