﻿#region using

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

#endregion

namespace ILProtectorUnpacker
{
    internal class Program
    {
        public static int IgnoreIndex = -1;
        public static AssemblyWriter AssemblyWriter;
        public static Assembly Assembly;
        public static MethodDef CurrentMethod;
        public static StackFrame[] MainFrames;
        public static List<TypeDef> JunkType = new List<TypeDef>();


        private static void Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "-i") IgnoreIndex = Convert.ToInt32(args[1]);
                Console.BackgroundColor = ConsoleColor.White;
                Console.ForegroundColor = ConsoleColor.Black;
                Console.WriteLine("*********************************");
                Console.WriteLine("***                           ***");
                Console.WriteLine("***    ILProtector Unpacker   ***");
                Console.WriteLine("***   V2.0.21.14 - V2.0.22.2  ***");
                Console.WriteLine("***     Coded By RexProg      ***");
                Console.WriteLine("***                           ***");
                Console.WriteLine("*********************************");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("[?] Enter Your Program Path : ");
                Console.ForegroundColor = ConsoleColor.Red;

                var path = Console.ReadLine();

                if (path == string.Empty)
                    return;
                if (path != null && path.StartsWith("\"") && path[path.Length - 1] == '"')
                    path = path.Substring(1, path.Length - 2);

                if (!File.Exists(path))
                {
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.WriteLine("[!] File not found");
                    Console.WriteLine("[!] Press key to exit...");
                    Console.Read();
                    return;
                }

                Console.ForegroundColor = ConsoleColor.DarkRed;

                AssemblyWriter = new AssemblyWriter(path);
                Assembly = Assembly.LoadFrom(path ?? throw new Exception("path is null"));
                Console.WriteLine("[+] Wait...");

                MainFrames = new StackTrace().GetFrames();
                Memory.Hook(
                    typeof(StackTrace).Module.GetType("System.Diagnostics.StackFrameHelper")
                        .GetMethod("GetMethodBase", BindingFlags.Instance | BindingFlags.Public),
                    typeof(Program).GetMethod("Hook4", BindingFlags.Instance | BindingFlags.Public));

                var types = AssemblyWriter.moduleDef.GetTypes();
                var list = types as IList<TypeDef> ?? types.ToList();

                var globalType = AssemblyWriter.moduleDef.GlobalType;

                var fieldMdToken = 0;

                foreach (var fieldDef in globalType.Fields)
                    if (fieldDef.Name == "Invoke")
                        fieldMdToken = fieldDef.MDToken.ToInt32();
                if (fieldMdToken == 0)
                    Console.WriteLine("[!] Couldn't find Invoke");

                var fieldValue = Assembly.Modules.FirstOrDefault()?.ResolveField(fieldMdToken).GetValue(null);

                var method = fieldValue?.GetType().GetMethod("Invoke");

                if (method == null)
                    Console.WriteLine("[!] Couldn't find InvokeMethod");

                InvokeDelegates(list, method, fieldValue);

                new StringDecrypter(Assembly).ReplaceStrings(list);

                foreach (var typeDef in JunkType) typeDef.DeclaringType.NestedTypes.Remove(typeDef);

                var methodDef = globalType.FindStaticConstructor();

                if (methodDef.HasBody)
                {
                    var startIndex = methodDef.Body.Instructions.IndexOf(methodDef.Body.Instructions.FirstOrDefault(
                                         inst =>
                                             inst.OpCode == OpCodes.Call
                                             && ((IMethod) inst.Operand).Name == "GetIUnknownForObject")) - 2;

                    var endindex = methodDef.Body.Instructions.IndexOf(methodDef.Body.Instructions.FirstOrDefault(
                                       inst =>
                                           inst.OpCode == OpCodes.Call
                                           && ((IMethod) inst.Operand).Name == "Release")) + 2;

                    methodDef.Body.ExceptionHandlers.Remove(methodDef.Body.ExceptionHandlers.FirstOrDefault(exh =>
                        exh.HandlerEnd == methodDef.Body.Instructions[endindex + 1]));

                    for (var i = startIndex; i <= endindex; i++)
                        methodDef.Body.Instructions.Remove(methodDef.Body.Instructions[startIndex]);
                }

                foreach (var meth in globalType.Methods.Where(met =>
                    met.ImplMap?.Module.Name.ToString() == "Protect32.dll" ||
                    met.ImplMap?.Module.Name.ToString() == "Protect64.dll").ToList())
                    globalType.Remove(meth);

                var invokeField = globalType.Fields.FirstOrDefault(fld => fld.Name == "Invoke");
                AssemblyWriter.moduleDef.Types.Remove(invokeField?.FieldType.ToTypeDefOrRef().ResolveTypeDef());
                globalType.Fields.Remove(invokeField);

                AssemblyWriter.Save();
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("[!] Program Unpacked");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] Exception :\n" + ex.Message);
            }

            Console.WriteLine("[!] Press key to exit...");
            Console.Read();
        }

        private static void InvokeDelegates(IList<TypeDef> typeDefs, MethodInfo invokeMethod, object invokeField)
        {
            foreach (var typeDef in typeDefs)
            foreach (var methodDef in typeDef.Methods)
                if (methodDef.Module.Name == Assembly.ManifestModule.ScopeName && methodDef.HasBody &&
                    methodDef.Body.Instructions.Count > 2 && methodDef.Body.Instructions[0].OpCode == OpCodes.Ldsfld &&
                    methodDef.Body.Instructions[0].Operand.ToString().Contains("Invoke") &&
                    methodDef.Body.Instructions[1].IsLdcI4())
                {
                    CurrentMethod = methodDef;

                    var mdToken = ((IType) methodDef.Body.Instructions[3].Operand).MDToken.ToInt32();
                    JunkType.Add(typeDef.NestedTypes.FirstOrDefault(net => net.MDToken.ToInt32() == mdToken));
                    var index = methodDef.Body.Instructions[1].GetLdcI4Value();
                    if (index == IgnoreIndex)
                        continue;

                    var method = invokeMethod.Invoke(invokeField, new object[] {index});

                    try
                    {
                        var dynamicMethodBodyReader = new DynamicMethodBodyReader(AssemblyWriter.moduleDef, method);
                        dynamicMethodBodyReader.Read();
                        var method2 = dynamicMethodBodyReader.GetMethod();
                        AssemblyWriter.WriteMethod(method2);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error in Read(): " + ex.Message + "\nMethod : " + method);
                    }
                }
        }

        public StackFrame Hook(int num)
        {
            var frames = new StackTrace().GetFrames();

            if (frames == null) return null;
            for (var i = 0; i < frames.Length; i++)
            {
                var method = frames[i].GetMethod();

                if (num == 0 && method.ToString().StartsWith("System.Delegate (")) return frames[i];
                if (num == 1 && method.ToString().StartsWith("System.Delegate ("))
                {
                    var value = Assembly.Modules.FirstOrDefault()?.ResolveMethod(CurrentMethod.MDToken.ToInt32());
                    typeof(StackFrame).GetField("method", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(frames[i + 1], value);
                    return frames[i + 1];
                }
            }

            return null;
        }

        public void Hook2(MethodBase mb)
        {
            typeof(StackFrame).GetField("method", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(this,
                    mb.Name == "InvokeMethod"
                        ? Assembly.Modules.FirstOrDefault()?.ResolveMethod(CurrentMethod.MDToken.ToInt32())
                        : mb);
        }

        public void Hook3(int iSkip, bool fNeedFileInfo, Thread targetThread, Exception e)
        {
            ///////////////////////////////////////////////////////////////////////////////////////////////
            //    FrameCount    |                2	                 |               int                 //
            //  METHODS_TO_SKIP |                0	                 |               int                 //
            //      frames      | {System.Diagnostics.StackFrame[6]} |  System.Diagnostics.StackFrame[]  //
            // m_iMethodsToSkip	|                4	                 |               int                 //
            //  m_iNumOfFrames  |                2	                 |               int                 //
            ///////////////////////////////////////////////////////////////////////////////////////////////
            typeof(StackFrame).GetField("method", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(
                    MainFrames.Last(),
                    Assembly.Modules.FirstOrDefault()?.ResolveMethod(CurrentMethod.MDToken.ToInt32()));

            var mainFramesList = MainFrames.ToList();

            for (var i = mainFramesList.Count; i < 6; i++)
                mainFramesList.Add(MainFrames.Last());
            for (var i = mainFramesList.Count; i > 6; i--)
                mainFramesList.Remove(mainFramesList.First());

            typeof(StackTrace).GetField("frames", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(this, mainFramesList.ToArray());
            typeof(StackTrace).GetField("m_iMethodsToSkip", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(this, 4);
            typeof(StackTrace).GetField("m_iNumOfFrames", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(this, 2);
        }

        public MethodBase Hook4(int i)
        {
            var rgMethodHandle = (IntPtr[]) typeof(StackTrace).Module
                .GetType("System.Diagnostics.StackFrameHelper")
                .GetField("rgMethodHandle", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(this);

            var methodHandleValue = rgMethodHandle[i];

            var runtimeMethodInfoStub =
                typeof(StackTrace).Module.GetType("System.RuntimeMethodInfoStub").GetConstructors()[1]
                    .Invoke(new object[] {methodHandleValue, this});

            var typicalMethodDefinition = typeof(StackTrace).Module.GetType("System.RuntimeMethodHandle")
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "GetTypicalMethodDefinition" && m.GetParameters().Length == 1).ToArray()[0]
                .Invoke(null, new[] {runtimeMethodInfoStub});

            var result = (MethodBase) typeof(StackTrace).Module.GetType("System.RuntimeType")
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "GetMethodBase" && m.GetParameters().Length == 1).ToArray()[0]
                .Invoke(null, new[] {typicalMethodDefinition});

            if (result.Name == "InvokeMethod")
                result = Assembly.Modules.FirstOrDefault()?.ResolveMethod(CurrentMethod.MDToken.ToInt32());
            return result;
        }
    }
}