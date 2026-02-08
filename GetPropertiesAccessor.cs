#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MongoOptions.Generator
{
    public record PropertyInfo
    {
        public string Name { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public bool IsRequired { get; set; }
        public string? GenericTypeOne { get; set; }
        public string? GenericTypeTwo { get; set; }
        public bool IsNewable { get; set; }
        public bool GenericTypeOneIsNewable { get; set; }
        public bool GenericTypeTwoIsNewable { get; set; }
        public IPropertySymbol? symbol { get; set; }
    }

    public class ClassInfo
    {
        public string ClassName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public List<PropertyInfo> Properties { get; set; } = [];
        public bool IsValidatorClass { get; set; }
        public bool IsInterface { get; set; }
        public List<string> WhiteList { get; set; } = [];
        public List<ClassInfo> InterfaceClasses { get; set; } = [];
    }

    [Generator]
    public class GetPropertiesAccessor : IIncrementalGenerator
    {       
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //Debugger.Launch(); 

            var externalDispatchers = context.CompilationProvider.Select((compilation, _) =>
            {
                var attributeSymbol = compilation.GetTypeByMetadataName("MongoOptions.Attributes.CustomDispatcherAttribute");
                if (attributeSymbol is null) return ImmutableArray<ClassInfo>.Empty;

                var result = new List<ClassInfo>();

                // Recursive helper to crawl through namespaces
                void ScanNamespace(INamespaceSymbol ns)
                {
                    foreach (var type in ns.GetTypeMembers())
                    {
                        if (type.TypeKind == TypeKind.Interface &&
                            type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol)))
                        {
                            var classinfo = GetProperties(type);
                            if (classinfo != null)
                                result.Add(classinfo); //type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                        }
                    }

                    foreach (var nestedNs in ns.GetNamespaceMembers())
                    {
                        ScanNamespace(nestedNs);
                    }
                }

                ScanNamespace(compilation.GlobalNamespace);
                return result.ToImmutableArray();
            });


            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is ClassDeclarationSyntax { AttributeLists.Count: > 0 } || s is InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: (ctx, _) => GetClassProperties(ctx))
                .Where(static m => m is not null);

            var combined = provider.Combine(externalDispatchers);

            context.RegisterSourceOutput(combined.Collect(), Execute);
        }

        private void Execute(SourceProductionContext context, ImmutableArray<(ClassInfo? Left, ImmutableArray<ClassInfo> Right)> array)
        {
            
            foreach (var combinedInfo in array)
            {
                if (combinedInfo.Left is null || combinedInfo.Left.IsInterface) continue;

                var info = combinedInfo.Left;

                info.InterfaceClasses = combinedInfo.Right.ToList();

                var lastDot = info.ClassName.LastIndexOf('.');
                var ns = lastDot > -1 ? info.ClassName.Substring(0, lastDot).Replace("global::", "") : "";
                var shortName = lastDot > -1 ? info.ClassName.Substring(lastDot + 1) : info.ClassName;

                var source = $@"
using System;
using System.Collections.Generic;
using MongoOptions;
using Microsoft.Extensions.Options;

{(string.IsNullOrEmpty(ns) ? "" : $"namespace {ns}")}
{{
    public partial class {shortName} : global::MongoOptions.Interfaces.IConfigFile
    {{
        public Type GetConfigType() => typeof({info.ClassName});
        public Type GetMonitorType() => typeof(IOptionsMonitor<{info.ClassName}>);
        public object Dispatcher(object model, object receiver)
            {{ 
                if (receiver is global::MongoOptions.Interfaces.IConfigDispatcherGateway<object> dispatcher)
                    return dispatcher.Execute<{info.ClassName}>(model);
                
                throw new NotSupportedException(""Dispatcher type unknown at compile time."");
            }}   
        public IEnumerable<global::MongoOptions.Types.PropertyMetadata> GetProperties()
        {{
{GeneratePropertyYields(info)}
        }}
    }}
}}";
                context.AddSource($"{shortName}_Metadata.g.cs", source);
            }
            
        }

        private string GeneratePropertyYields(ClassInfo info)
        {
            var sb = new StringBuilder();
            foreach (var prop in info.Properties)
            {
                var typeName = prop.TypeName;
                sb.AppendLine($@"            yield return new global::MongoOptions.Types.PropertyMetadata(
                ""{prop.Name}"",
                ""{prop.DisplayName}"",
                ""{prop.Description}"",
                typeof({typeName}),
                Getter: (obj) => (({info.ClassName})obj).{prop.Name},
                Setter: (obj, val) => (({info.ClassName})obj).{prop.Name} = ({typeName})val,
                ExpressionFactory: (instance) => {{
                    var typedInstance = ({info.FullName})instance;
                    return (global::System.Linq.Expressions.Expression<global::System.Func<{typeName}>>)(() => typedInstance.{prop.Name});}},
                Dispatcher: (model, receiver, self) => 
                    receiver.Execute<{typeName}>(model, self),
                DispatchGenericOne: (model, receiver, self) => 
                    receiver.Execute<{prop.GenericTypeOne ?? "object"}>(model, self),
                DispatchGenericTwo: (model, receiver, self) => 
                    receiver.Execute<{prop.GenericTypeOne ?? "object"}, {prop.GenericTypeTwo ?? "object"}>(model, self),");


                if (prop.IsNewable)
                {
                    sb.AppendLine($@"                New: () => new {typeName}(),");
                }
                else
                {
                    sb.AppendLine($@"                New: () => default({typeName}),");
                }

                if (prop.GenericTypeOne == null)
                {
                    sb.AppendLine($@"                NewTypePropertyOne: () => throw new InvalidOperationException(""Property {prop.Name} does not support NewTypePropertyOne.""),");
                    sb.AppendLine($@"                null,");
                }
                else
                {
                    if (prop.GenericTypeOneIsNewable)
                    {
                        sb.AppendLine($@"                NewTypePropertyOne: () => new {prop.GenericTypeOne}(),");
                    }
                    else
                    {
                        sb.AppendLine($@"                NewTypePropertyOne: () => default({prop.GenericTypeOne}),");
                        
                    }
                    sb.AppendLine($@"                typeof({prop.GenericTypeOne}),");
                }

                if (prop.GenericTypeTwo == null)
                {
                    sb.AppendLine($@"                NewTypePropertyTwo: () => throw new InvalidOperationException(""Property {prop.Name} does not support NewTypePropertyTwo.""),");
                    sb.AppendLine($@"                null,");
                }
                else
                {
                    if (prop.GenericTypeTwoIsNewable)
                    {
                        sb.AppendLine($@"                NewTypePropertyTwo: () => new {prop.GenericTypeTwo}(),");
                    }
                    else
                    {
                        sb.AppendLine($@"                NewTypePropertyTwo: () => default({prop.GenericTypeTwo}),");
                    }
                    sb.AppendLine($@"                typeof({prop.GenericTypeTwo}),");
                }

                BuildDispatcher(sb, info, prop);

                sb.AppendLine("            );");
            }
            return sb.ToString();
        }

        private StringBuilder BuildDispatcher(StringBuilder sb, ClassInfo info, PropertyInfo? prop)
        {
            string typeName = prop.TypeName;
            string typeNameTwo = "";
            if (prop.GenericTypeOne != null) 
                typeName = prop.GenericTypeOne;
            if (prop.GenericTypeTwo != null)
                typeNameTwo = ", " + prop.GenericTypeTwo;

            sb.Append($@"
                AotDispatch: (model, dispatcher, self) => 
                {{
                    if (dispatcher is global::MongoOptions.Interfaces.IDispatcherGateway<object> gateway)
                    {{
                        return gateway.Execute<{typeName}{typeNameTwo}>(model, self);
                    }}");

            foreach (var interfaces in info.InterfaceClasses)
            {
                if (IsTypeAllowed(prop.symbol, interfaces.WhiteList))
                {
                    sb.Append($@"
                    if (dispatcher is {interfaces.ClassName} {interfaces.FullName}ui)
                    {{
                        return {interfaces.FullName}ui.Execute<{typeName}{typeNameTwo}>(model, self);
                    }}
                    ");
                }
            }

            sb.AppendLine($@"
                    throw new NotSupportedException(""Dispatcher type unknown at compile time."");
                }}");
            return sb;
        }

        private static PropertyInfo MapProperty(IPropertySymbol prop)
        {
            var attributes = prop.GetAttributes();

            var displayAttr = attributes.FirstOrDefault(a => a.AttributeClass?.Name == "DisplayAttribute");

            // Look for [DisplayName("...")]
            var displayName = displayAttr?.NamedArguments
                .FirstOrDefault(arg => arg.Key == "Name")
                .Value.Value?.ToString();

            // 3. Safely extract "Description" (Note: check spelling "Description" vs "Discription")
            var description = displayAttr?.NamedArguments
                .FirstOrDefault(arg => arg.Key == "Description")
                .Value.Value?.ToString();
            
            var isNewableType = false;
            if (prop.Type is ITypeSymbol type)
            {
                isNewableType = IsNewable(type);
            }
            string? itemTypeNameOne = null;
            var itemTypeOneIsNewable = isNewableType;
            var itemTypeTwoIsNewable = isNewableType;
            string? itemTypeNameTwo = null;

            if (prop.Type is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                // For List<string>, TypeArguments[0] will be 'string'
                ITypeSymbol? itemType = namedType.TypeArguments.FirstOrDefault(); 
                
                ITypeSymbol? itemTypeTwo = null;

                if (namedType.TypeArguments.Length > 1)
                {
                    itemTypeTwo = namedType.TypeArguments[1];
                }

                if (itemType != null)
                {
                    // This gives you the full name (e.g., "System.String")
                    itemTypeNameOne = itemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    itemTypeOneIsNewable = IsNewable(itemType);
                    // Use this name to generate your AOT-safe casting code
                }
                if (itemTypeTwo != null)
                {
                    itemTypeNameTwo = itemTypeTwo.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    itemTypeTwoIsNewable = IsNewable(itemTypeTwo);
                }
            }

            return new PropertyInfo {
                Name = prop.Name,
                TypeName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                DisplayName = displayName ?? prop.Name, // Fallback to prop name
                Description = description ?? "",
                IsRequired = !prop.NullableAnnotation.HasFlag(NullableAnnotation.Annotated),
                GenericTypeOne = itemTypeNameOne,
                GenericTypeTwo = itemTypeNameTwo,
                IsNewable = isNewableType,
                GenericTypeOneIsNewable = itemTypeOneIsNewable,
                GenericTypeTwoIsNewable = itemTypeTwoIsNewable,
                symbol = prop
            };
        }

        private static ClassInfo? GetClassProperties(GeneratorSyntaxContext context)
        {
            INamedTypeSymbol? symbol = null;
            if (context.Node is ClassDeclarationSyntax classDecl)
            {
                symbol = context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            }
            
            if (context.Node is InterfaceDeclarationSyntax interfaceDecl)
            {
                symbol = context.SemanticModel.GetDeclaredSymbol(interfaceDecl) as INamedTypeSymbol;
            }

            if (symbol == null) return null;

            return GetProperties(symbol);
        }

        private static ClassInfo? GetProperties(INamedTypeSymbol symbol)
        {
            if (symbol == null) return null;

            var hasAttribute = symbol.GetAttributes()
                .Any(a => a.AttributeClass?.Name == "MongoOptionAttribute" ||
                          a.AttributeClass?.Name == "SubClassAttribute");

            var hasInterfaceAttribute = symbol.GetAttributes()
                .Any(a => a.AttributeClass?.Name == "CustomDispatcherAttribute");

            if (!hasAttribute && !hasInterfaceAttribute) return null;

            var displayAttr = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CustomDispatcherAttribute");
            string? whitelist = displayAttr?.NamedArguments
                .FirstOrDefault(arg => arg.Key == "WhiteList")
                .Value.Value?.ToString();
            List<string> WhiteList = whitelist?.Replace(" ", "").Split(',').ToList() ?? [];

            var properties = symbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public
                            && !p.IsStatic
                            && p.SetMethod is not null // Ensure it has a setter
                            && p.GetMethod is not null // Ensure it has a getter
                );

            var classInfo = new ClassInfo
            {
                ClassName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                FullName = symbol.Name,
                Properties = [],
                WhiteList = WhiteList,
                IsInterface = hasInterfaceAttribute
            };

            foreach (var property in properties) {
                classInfo.Properties.Add(MapProperty(property));
            }

            return classInfo;
        }

        private static bool IsNewable(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_String)
                return false;

            if (type.IsValueType)
                return true;

            if (type is INamedTypeSymbol namedType)
            {
                if (namedType.IsAbstract)
                    return false;

                return namedType.InstanceConstructors.Any(c =>
                    c.Parameters.Length == 0 &&
                    c.DeclaredAccessibility == Accessibility.Public);
            }

            return false;
        }

        private static bool IsAllowed(IPropertySymbol propertySymbol, List<string> allowedTypes)
        {
            if (allowedTypes.Count == 0) return true; 
            ITypeSymbol propertyType = propertySymbol.Type;

            // 1. Get the simple name (e.g., "String" or "MyEnum")
            string typeName = propertyType.Name;

            // 2. Check if it's an Enum generally
            bool isEnum = propertyType.TypeKind == TypeKind.Enum;

            // 3. Perform the validation
            return allowedTypes.Contains(typeName) ||
                (allowedTypes.Contains("Enum") && isEnum);
        }

        private static bool IsTypeAllowed(IPropertySymbol propertySymbol, List<string> allowedTypes)
        {
            if (allowedTypes.Count == 0) return true;
            ITypeSymbol type = propertySymbol.Type;

            // 1. Unwrap Nullables first
            if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                type = named.TypeArguments[0];

            foreach (var allowed in allowedTypes)
            {
                // Check for special "Keyword" match
                if (allowed == "Enum" && type.TypeKind == TypeKind.Enum) return true;

                // Check for exact name match (e.g., "Int32", "AnotherTest")
                if (string.Equals(type.Name, allowed, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
