﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace MemoryPack.Generator;

// dotnet/runtime generators.

// https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.RegularExpressions/gen/
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Text.Json/gen
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Private.CoreLib/gen
// https://github.com/dotnet/runtime/tree/main/src/libraries/Microsoft.Extensions.Logging.Abstractions/gen
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Runtime.InteropServices.JavaScript/gen/JSImportGenerator
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Runtime.InteropServices/gen/LibraryImportGenerator
// https://github.com/dotnet/runtime/tree/main/src/tests/Common/XUnitWrapperGenerator

// documents, blogs.

// https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md
// https://andrewlock.net/creating-a-source-generator-part-1-creating-an-incremental-source-generator/
// https://qiita.com/WiZLite/items/48f37278cf13be899e40
// https://zenn.dev/pcysl5edgo/articles/6d9be0dd99c008
// https://neue.cc/2021/05/08_600.html
// https://www.thinktecture.com/en/net/roslyn-source-generators-introduction/

// for check generated file
// <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
// <CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>

[Generator(LanguageNames.CSharp)]
public partial class MemoryPackGenerator : IIncrementalGenerator
{
    public const string MemoryPackableAttributeFullName = "MemoryPack.MemoryPackableAttribute";
    public const string GenerateTypeScriptAttributeFullName = "MemoryPack.GenerateTypeScriptAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // no need RegisterPostInitializationOutput

        // return dir of info output or null .
        var logProvider = context.AnalyzerConfigOptionsProvider
            .Select((configOptions, token) =>
            {
                if (configOptions.GlobalOptions.TryGetValue("build_property.MemoryPackGenerator_SerializationInfoOutputDirectory", out var path))
                {
                    return path;
                }

                return (string?)null;
            });

        var typeDeclarations = context.SyntaxProvider.ForAttributeWithMetadataName(
                MemoryPackableAttributeFullName,
                predicate: static (node, token) =>
                {
                    // search [MemoryPackable] class or struct or interface or record
                    return (node is ClassDeclarationSyntax
                                 or StructDeclarationSyntax
                                 or RecordDeclarationSyntax
                                 or InterfaceDeclarationSyntax);
                },
                transform: static (context, token) =>
                {
                    return (TypeDeclarationSyntax)context.TargetNode;
                });

        var source = typeDeclarations
            .Combine(context.CompilationProvider)
            .WithComparer(Comparer.Instance)
            .Combine(logProvider);

        context.RegisterSourceOutput(source, static (context, source) =>
        {
            var typeDeclaration = source.Left.Item1;
            var compilation = source.Left.Item2;
            var logPath = source.Right;

            Generate(typeDeclaration, compilation, logPath, new GeneratorContext(context));
        });

        // TypeScript generation
        RegisterTypeScript(context);
    }

    void RegisterTypeScript(IncrementalGeneratorInitializationContext context)
    {
        var typeScriptEnabled = context.AnalyzerConfigOptionsProvider
            .Select((configOptions, token) =>
            {
                if (configOptions.GlobalOptions.TryGetValue("build_property.MemoryPackGenerator_TypeScriptOutputDirectory", out var path))
                {
                    return path;
                }

                return (string?)null;
            });

        var typeScriptDeclarations = context.SyntaxProvider.ForAttributeWithMetadataName(
                GenerateTypeScriptAttributeFullName,
                predicate: static (node, token) =>
                {
                    return (node is ClassDeclarationSyntax
                                 or RecordDeclarationSyntax
                                 or InterfaceDeclarationSyntax);
                },
                transform: static (context, token) =>
                {
                    return (TypeDeclarationSyntax)context.TargetNode;
                });

        var typeScriptGenerateSource = typeScriptDeclarations
            .Combine(context.CompilationProvider)
            .WithComparer(Comparer.Instance)
            .Combine(typeScriptEnabled)
            .Where(x => x.Right != null) // filter, exists TypeScriptOutputDirectory
            .Collect();

        context.RegisterSourceOutput(typeScriptGenerateSource, static (context, source) =>
        {
            ReferenceSymbols? reference = null;
            string? generatePath = null;

            var unionMap = new Dictionary<ITypeSymbol, ITypeSymbol>(SymbolEqualityComparer.Default); // <impl, base>
            foreach (var item in source)
            {
                var syntax = item.Left.Item1;
                var compilation = item.Left.Item2;
                var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
                var typeSymbol = semanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) as ITypeSymbol;
                if (typeSymbol == null) continue;
                if (reference == null)
                {
                    reference = new ReferenceSymbols(compilation);
                }

                var isUnion = typeSymbol.ContainsAttribute(reference.MemoryPackUnionAttribute);

                if (isUnion)
                {
                    var unionTags = typeSymbol.GetAttributes()
                        .Where(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, reference.MemoryPackUnionAttribute))
                        .Where(x => x.ConstructorArguments.Length == 2)
                        .Select(x => (INamedTypeSymbol)x.ConstructorArguments[1].Value!);
                    foreach (var implType in unionTags)
                    {
                        unionMap[implType] = typeSymbol;
                    }
                }
            }

            var generatedEnums = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var generatedTypes = new List<TypeMeta>();
            foreach (var item in source)
            {
                var typeDeclaration = item.Left.Item1;
                var compilation = item.Left.Item2;
                var path = generatePath = item.Right!;

                if (reference == null)
                {
                    reference = new ReferenceSymbols(compilation);
                }

                var meta = GenerateTypeScript(typeDeclaration, compilation, path, context, reference, unionMap, generatedEnums);
                if (meta != null)
                {
                    generatedTypes.Add(meta);
                }
            }

            if (generatePath != null && generatedTypes.Count != 0)
            {
                GenerateEnums(generatedEnums, generatePath);

                // generate runtime
                var runtime = new[]{
                    ("MemoryPackWriter.ts", TypeScriptRuntime.MemoryPackWriter),
                    ("MemoryPackReader.ts", TypeScriptRuntime.MemoryPackReader),
                };

                foreach (var item in runtime)
                {
                    var filePath = Path.Combine(generatePath, item.Item1);
                    if (!File.Exists(filePath))
                    {
                        File.WriteAllText(filePath, item.Item2, new UTF8Encoding(false));
                    }
                }
            }
        });
    }

    class Comparer : IEqualityComparer<(TypeDeclarationSyntax, Compilation)>
    {
        public static readonly Comparer Instance = new Comparer();

        public bool Equals((TypeDeclarationSyntax, Compilation) x, (TypeDeclarationSyntax, Compilation) y)
        {
            return x.Item1.Equals(y.Item1);
        }

        public int GetHashCode((TypeDeclarationSyntax, Compilation) obj)
        {
            return obj.Item1.GetHashCode();
        }
    }

    class GeneratorContext : IGeneratorContext
    {
        SourceProductionContext context;

        public GeneratorContext(SourceProductionContext context)
        {
            this.context = context;
        }

        public CancellationToken CancellationToken => context.CancellationToken;

        public void AddSource(string hintName, string source)
        {
            context.AddSource(hintName, source);
        }

        public void ReportDiagnostic(Diagnostic diagnostic)
        {
            context.ReportDiagnostic(diagnostic);
        }
    }
}
