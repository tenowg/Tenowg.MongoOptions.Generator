#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MongoOptions.Generator
{
    [Generator]
    public class MetadataBuilder : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => s is ClassDeclarationSyntax { AttributeLists.Count: > 0 } || s is InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
                    transform: (ctx, _) =>
                    {
                        INamedTypeSymbol? symbol = null;
                        if (ctx.Node is ClassDeclarationSyntax classDecl)
                        {
                            symbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                        } else
                        {
                            return (null, null, null);
                        }

                        var mongoAttr = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "OptionsMetadataAttribute");

                        INamedTypeSymbol? isRecord = mongoAttr?.NamedArguments
                            .FirstOrDefault(arg => arg.Key == "MetadataRecord")
                            .Value.Value as INamedTypeSymbol ?? null;

                        //string[]? isStrings = mongoAttr?.NamedArguments
                        //    .FirstOrDefault(arg => arg.Key == "MetadataNames")
                        //    .Value.Values.Select(v => v.Value as string ?? "").ToArray() ?? null;

                        var properties = isRecord?.GetMembers()
                            .OfType<IPropertySymbol>()
                            .Where(p => p.DeclaredAccessibility == Accessibility.Public
                                        && !p.IsStatic
                                        && p.SetMethod is not null // Ensure it has a setter
                                        && p.GetMethod is not null // Ensure it has a getter
                            );
                        return (symbol, properties, isRecord);
                    })
                .Where(static m => m.properties is not null);

            context.RegisterSourceOutput(provider.Collect(), Execute);
        }

        private void Execute(SourceProductionContext context, ImmutableArray<(INamedTypeSymbol symbol, IEnumerable<IPropertySymbol>? properties, INamedTypeSymbol? isRecord)> array)
        {
            foreach (var item in array)
            {
                var source = new StringBuilder();

                var lastDot = item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).LastIndexOf('.');
                var ns = lastDot > -1 ? item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Substring(0, lastDot).Replace("global::", "") : "";
                var shortName = lastDot > -1 ? item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Substring(lastDot + 1) : item.symbol.Name;

                source.Append($@"
namespace {ns}
{{
    public class {shortName}OptionsMetadataFilterBuilder
    {{
        internal readonly List<global::MongoDB.Driver.FilterDefinition<global::MongoOptions.Data.ConfigDocument<{shortName}>>> _filters = new();
    ");
                foreach (var prop in item.properties!)
                {
                    source.Append($@"
        public global::MongoOptions.Generator.MetadataFieldFilter<{shortName}OptionsMetadataFilterBuilder, {prop.Type.ToDisplayString()}, {item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}> {prop.Name}
            => new(this, _filters, ""Metadata.{prop.Name}"");
        ");
                }

                // Combines them into an AND filter
                source.Append($@"
        public {shortName}OptionsMetadataFilterBuilder Or(
                params Func<{shortName}OptionsMetadataFilterBuilder, {shortName}OptionsMetadataFilterBuilder>[] branches)
        {{
            var orFilters = new List<global::MongoDB.Driver.FilterDefinition<global::MongoOptions.Data.ConfigDocument<{item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>>>();
        
            foreach (var branch in branches)
            {{
                var subBuilder = new {shortName}OptionsMetadataFilterBuilder();
                branch(subBuilder); // Execute the lambda
            
                // Combine the branch's filters into an AND (if there are multiple)
                orFilters.Add(subBuilder._filters.Count == 1 
                    ? subBuilder._filters[0] 
                    : global::MongoDB.Driver.Builders<global::MongoOptions.Data.ConfigDocument<{item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>>.Filter.And(subBuilder._filters));
            }}

            _filters.Add(global::MongoDB.Driver.Builders<global::MongoOptions.Data.ConfigDocument<{item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>>.Filter.Or(orFilters));
            return this;
        }}

        internal global::MongoDB.Driver.FilterDefinition<global::MongoOptions.Data.ConfigDocument<{item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>> Build() =>
            global::MongoDB.Driver.Builders<global::MongoOptions.Data.ConfigDocument<{item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>>.Filter.And(_filters.ToArray());
    }}
}}");
                context.AddSource($"{shortName}_Metadata_Builder.g.cs", source.ToString());

                var source2 = new StringBuilder();

                source2.Append($@"
namespace {ns}
{{
    public static class {shortName}OptionsManagerExtensions
    {{
        public static async Task<IEnumerable<string>> GetKeysAsync{shortName}(
            this global::MongoOptions.Interfaces.IConfigManager manager, 
            Func<{shortName}OptionsMetadataFilterBuilder, {shortName}OptionsMetadataFilterBuilder> filterFunc)
        {{
            var builder = new {shortName}OptionsMetadataFilterBuilder();
            filterFunc(builder);
            var filterDef = builder.Build();

            // Pass filterDef to your underlying MongoDB collection
            return await manager.GetKeysBuilder<{item.symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}, {item.isRecord.Name}>(filterDef);
        }}
    }}
}}");
                context.AddSource($"{shortName}_Metadata_Extensions.g.cs", source2.ToString());

                var source3 = new StringBuilder();

                source3.Append($@"
namespace TrueCompanion.Shared.MongoObjects
{{
    public partial class {shortName} : global::MongoOptions.Interfaces.IOptionsMetadata<{item.isRecord.Name}>
    {{
        public Dictionary<string, object> GetMetadata({item.isRecord.Name} metadata) 
        {{
            var metadict = new Dictionary<string, object>();
            ");
                foreach (var prop in item.properties!)
                {
                    source3.Append($@"
                    metadict[""{prop.Name}""] = metadata.{prop.Name};");
                }
                source3.Append($@"
            return metadict;
        }}
    }}
}}");

                context.AddSource($"{shortName}_Metadata_Output.g.cs", source3.ToString());
            }
        }

        private static void GetMembersAndGenerate(INamedTypeSymbol targetType, GeneratorAttributeSyntaxContext context)
        {
            // Get ALL members (fields, properties, methods, etc.)
            var allMembers = targetType.GetMembers();

            // Most common: only public instance properties (great for records/classes)
            var properties = allMembers
                .OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public
                            && !p.IsStatic
                            && p.GetMethod != null)           // has a getter
                .ToImmutableArray();

            // You can also get:
            // - Fields:     .OfType<IFieldSymbol>()
            // - Methods:    .OfType<IMethodSymbol>()
            // - All members: targetType.GetMembers()

            // Example: build some generation data
            foreach (var prop in properties)
            {
                var propName = prop.Name;
                var propType = prop.Type.ToDisplayString(); // e.g. "string", "int", "MyOtherRecord"
                var isNullable = prop.NullableAnnotation == NullableAnnotation.Annotated;

                // ... collect into a model and generate source
            }

            // Then emit code using context or SourceProductionContext
        }
    }
}
