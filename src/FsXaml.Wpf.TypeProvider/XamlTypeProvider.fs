﻿namespace FsXaml

open System
open System.Reflection
open System.IO
open System.Collections.Generic
open System.ComponentModel
open System.Xml
open System.Windows
open System.Windows.Data

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Reflection
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Quotations.ExprShape

open Samples.FSharp.ProvidedTypes
open FsXaml.TypeProviders.Helper

open System.Reflection

[<assembly:AssemblyVersion("0.9.3.0")>]
[<assembly:AssemblyFileVersion("0.9.3.0")>]
do()


module XamlTypeUtils =
    let internal wpfAssembly = typeof<System.Windows.Controls.Button>.Assembly
    let internal rootNamespace = typeof<XamlFileAccessor>.Namespace

    type internal FilePosition =  
       { Line: int; 
         Column: int;
         FileName: string }

    type internal XamlNode =
        { Position: FilePosition
          IsRoot: bool
          Name: string
          NodeType : Type }

    let internal posOfReader filename (xaml:XmlReader) = 
        let lineInfo = xaml :> obj :?> IXmlLineInfo
        { Line = lineInfo.LineNumber
          Column = lineInfo.LinePosition
          FileName = filename }

    type RootNodeType =
    | FrameworkElement
    | ResourceDictionary

    let internal createXamlNode (schemaContext: Xaml.XamlSchemaContext) filename isRoot (xaml:XmlReader) (rootNodeType : RootNodeType option) =
        let pos = posOfReader filename xaml
        try 
            let name =                        
                match rootNodeType with
                | Some FrameworkElement ->
                    match xaml.GetAttribute("Name") with
                    | name when not(String.IsNullOrWhiteSpace(name)) -> Some name
                    | _ ->
                        match xaml.GetAttribute("x:Name") with
                        | name when not(String.IsNullOrWhiteSpace(name)) -> Some name
                        | _ -> if isRoot then Some "Root" else None
                | Some ResourceDictionary ->
                    match xaml.GetAttribute("x:Key") with
                    | name when not(String.IsNullOrWhiteSpace(name)) -> Some name
                    | _ -> if isRoot then Some "Root" else None
                | None ->
                    match xaml.GetAttribute("x:Name") with
                    | name when not(String.IsNullOrWhiteSpace(name)) -> Some name
                    | _ -> if isRoot then Some "Root" else None

            match name with
            | None -> None
            | Some name -> 
                let propertyType =
                    // TODO: Add in other missing resource types as discovered.  For now, Color + Brush types aren't found, and need to be handled
                    match xaml.LocalName with
                    | "Color" -> typeof<System.Windows.Media.Color>
                    | "SolidColorBrush" -> typeof<System.Windows.Media.SolidColorBrush>
                    | "BitmapCacheBrush" -> typeof<System.Windows.Media.BitmapCacheBrush>
                    | "LinearGradientBrush" -> typeof<System.Windows.Media.LinearGradientBrush>
                    | "RadialGradientBrush" -> typeof<System.Windows.Media.RadialGradientBrush>
                    | "DrawingBrush" -> typeof<System.Windows.Media.DrawingBrush>
                    | "ImageBrush" -> typeof<System.Windows.Media.ImageBrush>
                    | "VisualBrush" -> typeof<System.Windows.Media.VisualBrush>
                    | _ ->
                        let r = schemaContext.GetAllXamlTypes(xaml.NamespaceURI)
                        let xamltype = r |> Seq.tryFind (fun xt -> xt.Name = xaml.LocalName)
                        match xamltype with
                        | None   -> typeof<obj>
                        | Some t -> t.UnderlyingType
                { Position = pos
                  IsRoot = isRoot
                  Name = name
                  NodeType = propertyType }
                |> Some
        with
        | :? XmlException -> failwithf "Error near %A" pos

    let internal readXamlFile (schemaContext: Xaml.XamlSchemaContext) filename (xaml:XmlReader) =    
        seq {
            let isRoot = ref true
            let fileType = ref FrameworkElement
            while xaml.Read() do
                match xaml.NodeType with
                | XmlNodeType.Element ->
                    match !isRoot with
                    | true -> 
                        let node = createXamlNode schemaContext filename (!isRoot) xaml None
                        match node with
                        | Some node ->
                            yield node
                            if (node.NodeType = typeof<ResourceDictionary>) then 
                                fileType := ResourceDictionary
                            isRoot := false
                        | None -> ()
                    | false -> 
                        let node = createXamlNode schemaContext filename (!isRoot) xaml (Some !fileType)
                        match node with
                        | Some node -> yield node
                        | None -> ()
                | XmlNodeType.EndElement | XmlNodeType.Comment | XmlNodeType.Text -> ()
                | unexpected -> failwithf "Unexpected node type %A at %A" unexpected (posOfReader filename xaml) }

    let createXmlReader(textReader:TextReader) =
        XmlReader.Create(textReader, XmlReaderSettings(IgnoreProcessingInstructions = true, IgnoreWhitespace = true))

    let internal readElements (schemaContext: Xaml.XamlSchemaContext) (reader: TextReader) fileName =
        let elements = 
            reader
            |> createXmlReader 
            |> readXamlFile schemaContext fileName
            |> Seq.toList
        elements

    let internal createFrameworkElementAccessorTypeFromElements (assembly : ProvidedAssembly)  typeName fileName (resourcePath : string) elements root =        
        let xamlType = ProvidedTypeDefinition(typeName, Some typeof<obj>, IsErased = false)
        assembly.AddTypes [ xamlType ]
        xamlType.AddDefinitionLocation(root.Position.Line,root.Position.Column,root.Position.FileName)

        let xf = ProvidedField("xamlFileAccessor", typeof<XamlFileAccessor>)
        xf.SetFieldAttributes(FieldAttributes.InitOnly)
        xamlType.AddMember xf

        let accessExpr node (args:Expr list) =
            let name = node.Name
            let expr = if node.IsRoot then <@@ (%%Expr.FieldGet(args.[0], xf) : XamlFileAccessor).Root @@> else <@@ (%%Expr.FieldGet(args.[0], xf) : XamlFileAccessor).GetChild name @@>
            Expr.Coerce(expr,node.NodeType)
        
        let xamlFileProp = ProvidedProperty("XamlFileAccessor", typeof<XamlFileAccessor>)
        xamlFileProp.GetterCode <- fun args -> Expr.FieldGet(args.[0], xf)
        xamlType.AddMember xamlFileProp

        let ctor = 
            ProvidedConstructor(
                parameters = [ ProvidedParameter("rootElement",typeof<FrameworkElement>) ],
                InvokeCode = (fun args -> Expr.FieldSet(args.[0], xf, <@@ XamlFileAccessor((%%args.[1] : FrameworkElement)) @@>)))

        ctor.AddXmlDoc (sprintf "Initializes typed access to %s" fileName)
        ctor.AddDefinitionLocation(root.Position.Line,root.Position.Column,root.Position.FileName)
        xamlType.AddMember ctor

        for node in elements do
            let property = 
                ProvidedProperty(
                    propertyName = node.Name,
                    propertyType = node.NodeType,
                    GetterCode = accessExpr node)
            property.AddXmlDoc(sprintf "Gets the %s element" node.Name)
            property.AddDefinitionLocation(root.Position.Line,root.Position.Column,root.Position.FileName)
            xamlType.AddMember property
        xamlType 

    let internal createResourceDictionaryAccessorTypeFromElements (assembly : ProvidedAssembly)  typeName fileName (resourcePath : string) elements root =
        let xamlType = ProvidedTypeDefinition(typeName, Some typeof<obj>, IsErased = false)
        assembly.AddTypes [ xamlType ]
        xamlType.AddDefinitionLocation(root.Position.Line,root.Position.Column,root.Position.FileName)

        let xf = ProvidedField("xamlResourceAccessor", typeof<XamlResourceAccessor>)
        xf.SetFieldAttributes(FieldAttributes.InitOnly)
        xamlType.AddMember xf

        let accessExpr node (args:Expr list) =
            let name = node.Name
            let expr = if node.IsRoot then <@@ (%%Expr.FieldGet(args.[0], xf) : XamlResourceAccessor).Root @@> else <@@ (%%Expr.FieldGet(args.[0], xf) : XamlResourceAccessor).GetResource name @@>
            Expr.Coerce(expr,node.NodeType)
        
        let xamlFileProp = ProvidedProperty("XamlResourceAccessor", typeof<XamlResourceAccessor>)
        xamlFileProp.GetterCode <- fun args -> Expr.FieldGet(args.[0], xf)
        xamlType.AddMember xamlFileProp

        let ctor = 
            ProvidedConstructor(
                parameters = [ ProvidedParameter("rootElement",typeof<System.Windows.ResourceDictionary>) ],
                InvokeCode = (fun args -> Expr.FieldSet(args.[0], xf, <@@ XamlResourceAccessor((%%args.[1] : ResourceDictionary)) @@>)))

        ctor.AddXmlDoc (sprintf "Initializes typed access to %s" fileName)
        ctor.AddDefinitionLocation(root.Position.Line,root.Position.Column,root.Position.FileName)
        xamlType.AddMember ctor

        for node in elements do
            let property = 
                ProvidedProperty(
                    propertyName = node.Name,
                    propertyType = node.NodeType,
                    GetterCode = accessExpr node)
            property.AddXmlDoc(sprintf "Gets the %s element" node.Name)
            property.AddDefinitionLocation(root.Position.Line,root.Position.Column,root.Position.FileName)
            xamlType.AddMember property
        xamlType 

    let internal createAccessorTypeFromElements (assembly : ProvidedAssembly)  typeName fileName (resourcePath : string) elements =
        let root = List.head elements        
        let accessor = 
            match root.NodeType with
            | rd when rd = typeof<System.Windows.ResourceDictionary> -> createResourceDictionaryAccessorTypeFromElements
            | _ -> createFrameworkElementAccessorTypeFromElements 
        accessor assembly typeName fileName resourcePath elements root

[<TypeProvider>]
type public XamlTypeProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let assembly = Assembly.GetExecutingAssembly()
    let nameSpace = this.GetType().Namespace
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "XAML", Some typeof<obj>, IsErased = false)
    let missingValue = "@@@missingValue###"
    
    let assemblies = 
        config.ReferencedAssemblies 
        |> Seq.map (fun r -> Assembly.Load(IO.File.ReadAllBytes r))
        |> Seq.append [XamlTypeUtils.wpfAssembly] //; typeof<XamlFile>.Assembly]
        |> Array.ofSeq
        
    let ss = 
        let scontext = Xaml.XamlSchemaContextSettings()
        scontext.FullyQualifyAssemblyNamesInClrNamespaces <- false
        scontext.SupportMarkupExtensionsWithDuplicateArity <- false
        scontext

    let schemaContext = System.Xaml.XamlSchemaContext(assemblies, ss)//  (assemblies)

    do System.AppDomain.CurrentDomain.add_AssemblyResolve(fun _ args ->
        let name = System.Reflection.AssemblyName(args.Name)
        let existingAssembly = 
            System.AppDomain.CurrentDomain.GetAssemblies()
            |> Seq.tryFind(fun a -> System.Reflection.AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))
        match existingAssembly with
        | Some a -> a
        | None -> null
        )

    do 
        // tempAssembly.AddTypes <| [ providerType ]
        providerType.DefineStaticParameters(
            parameters = [ ProvidedStaticParameter("XamlResourceLocation", typeof<string>) ], 
            instantiationFunction = (fun typeName parameterValues ->   
                let resourcePath = string parameterValues.[0]
                let resolvedFileName = findConfigFile config.ResolutionFolder resourcePath
                watchForChanges this resolvedFileName

                use reader = new StreamReader(resolvedFileName)                            
                let elements = XamlTypeUtils.readElements schemaContext reader resolvedFileName
            
                let root = List.head elements
                
                let ucType = typeof<System.Windows.Controls.UserControl>

                let outerType =
                    let createFactoryType factoryType =
                        let outertype = ProvidedTypeDefinition(assembly, nameSpace, typeName, Some(factoryType), IsErased = false)
                        let ctor = ProvidedConstructor([])
                        ctor.BaseConstructorCall <- fun args -> factoryType.GetConstructors().[0], [Expr.Value(resourcePath)]
                        ctor.InvokeCode <- fun args -> <@@ 0 @@>
                        outertype.AddMember ctor
                        outertype
                    match root.NodeType with
                    | win when win = typeof<System.Windows.Window> ->
                        createFactoryType typeof<XamlTypeFactory<System.Windows.Window>>
                    | app when app = typeof<System.Windows.Application> ->
                        createFactoryType(typeof<XamlTypeFactory<System.Windows.Application>>)
                    | rd when rd = typeof<System.Windows.ResourceDictionary> ->
                        createFactoryType(typeof<XamlTypeFactory<System.Windows.ResourceDictionary>>)
                    | _ ->
                        createFactoryType(typeof<XamlContainer>)
                
                let tempAssembly = ProvidedAssembly(Path.ChangeExtension(Path.GetTempFileName(), ".dll"))

                tempAssembly.AddTypes <| [ outerType ]
                outerType.AddMembersDelayed <| fun() ->
                    [                                                                                                                            
                        yield XamlTypeUtils.createAccessorTypeFromElements tempAssembly "Accessor" resolvedFileName resourcePath elements
                    ] 
                outerType))

        this.AddNamespace(nameSpace, [ providerType ])

[<assembly:TypeProviderAssembly>] 
do()