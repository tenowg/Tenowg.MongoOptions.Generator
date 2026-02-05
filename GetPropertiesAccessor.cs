using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace MongoOptions.Generator
{
    public record PropertyInfo
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public bool IsRequired { get; set; }
    }

    public class ClassInfo
    {
        public string ClassName { get; set; }
        public string FullName { get; set; }
        public List<PropertyInfo> Properties { get; set; }
        public bool IsValidatorClass { get; set; }
    }

    [Generator]
    public class GetPropertiesAccessor : IIncrementalGenerator
    {
        private int indent = 4;
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //Debugger.Launch();
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: (ctx, _) => GetProperties(ctx))
                .Where(static m => m is not null);

            context.RegisterSourceOutput(provider.Collect(), Execute);
        }

        private void Execute(SourceProductionContext context, ImmutableArray<ClassInfo> array)
        {
            
            foreach (var info in array)
            {
                if (info is null) continue;

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
        public object Dispatcher(object model, global::MongoOptions.Interfaces.IClassDispatcher receiver) => 
                    receiver.Execute<{shortName}>(model);
        public IEnumerable<PropertyMetadata> GetProperties()
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
                sb.AppendLine($@"            yield return new PropertyMetadata(
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
                    receiver.Execute<{typeName}>(model, self)
            );");
            }
            return sb.ToString();
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
            prop.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new PropertyInfo {
                Name = prop.Name,
                TypeName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                DisplayName = displayName ?? prop.Name, // Fallback to prop name
                Description = description ?? "",
                IsRequired = !prop.NullableAnnotation.HasFlag(NullableAnnotation.Annotated) // AOT-safe required check
            }; ;
        }

        private static ClassInfo? GetProperties(GeneratorSyntaxContext context)
        {
            var classDecl = (ClassDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

            if (symbol == null) return null;

            var hasAttribute = symbol.GetAttributes()
                .Any(a => a.AttributeClass?.Name == "MongoOptionAttribute" ||
                          a.AttributeClass?.Name == "SubClassAttribute");

            if (!hasAttribute) return null;

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
                Properties = []
            };

            foreach (var property in properties) {
                classInfo.Properties.Add(MapProperty(property));
            }

            return classInfo;
        }

        private StringBuilder AppendWithIndent(StringBuilder sb, string Value, int tabs)
        {
            sb.Append(' ', indent * tabs);
            sb.Append(Value + "\n");
            return sb;
        }
    }
}
