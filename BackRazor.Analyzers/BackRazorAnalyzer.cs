using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace BackRazor.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BackRazorAnalyzer : DiagnosticAnalyzer
{
    private static DiagnosticDescriptor Rule(string id, string title, string message) =>
        new(id, title, message, "BackRazor", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Component = Rule("BRZ001", "Live objects are Razor components",
        "[BackRazor] type '{0}' must derive from ComponentBase");
    private static readonly DiagnosticDescriptor Disposal = Rule("BRZ002", "Owned resources require a disposal contract",
        "Live object '{0}' owns disposable fields and must implement IDisposable or IAsyncDisposable");
    private static readonly DiagnosticDescriptor Dependency = Rule("BRZ003", "Live objects are browser independent",
        "Live object code cannot depend on browser/request type '{0}'");
    private static readonly DiagnosticDescriptor AfterRender = Rule("BRZ004", "Live objects have no browser completion lifecycle",
        "Live object cannot implement browser completion method '{0}'");
    private static readonly DiagnosticDescriptor Mutation = Rule("BRZ005", "Rendering must not write state",
        "Rendering cannot write '{0}'; change state in an explicit command or lifecycle method");
    private static readonly DiagnosticDescriptor RenderCall = Rule("BRZ006", "Render calls must be verifiable",
        "Rendering cannot use unverified member '{0}'; use source-visible pure helpers or supported value formatting");
    private static readonly DiagnosticDescriptor StaticState = Rule("BRZ007", "State belongs to a live instance",
        "Live object field '{0}' cannot be static unless it is const");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Component, Disposal, Dependency, AfterRender, Mutation, RenderCall, StaticState);

    public override void Initialize(AnalysisContext context)
    {
        // Razor components arrive as generated C#; skipping generated code disables these rules.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var markedTypes = new ConcurrentBag<INamedTypeSymbol>();
            var bodies = new ConcurrentDictionary<IMethodSymbol, ImmutableArray<IOperation>>(SymbolEqualityComparer.Default);
            start.RegisterSymbolAction(symbol =>
            {
                if (!IsMarked((INamedTypeSymbol)symbol.Symbol)) return;
                AnalyzeType(symbol);
                markedTypes.Add((INamedTypeSymbol)symbol.Symbol);
            }, SymbolKind.NamedType);
            start.RegisterSyntaxNodeAction(AnalyzeDependency, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
            start.RegisterOperationBlockAction(block =>
            {
                if (block.OwningSymbol is IMethodSymbol method)
                    bodies.TryAdd(method.OriginalDefinition, block.OperationBlocks);
            });
            start.RegisterCompilationEndAction(end =>
            {
                foreach (var type in markedTypes)
                {
                    var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                    foreach (var render in type.GetMembers("BuildRenderTree").OfType<IMethodSymbol>().Where(method => method.IsOverride))
                        AnalyzeRenderMethod(render, render.Locations.First(), end, bodies, visited);
                }
            });
        });
    }

    private static bool IsMarked(INamedTypeSymbol type) => type.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "BackRazor.BackRazorAttribute");

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var marker = type.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "BackRazor.BackRazorAttribute");
        if (marker is null) return;
        var location = marker.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
            ?? type.Locations.First();

        if (!IsOrDerivesFrom(type, "Microsoft.AspNetCore.Components.ComponentBase"))
            context.ReportDiagnostic(Diagnostic.Create(Component, location, type.Name));

        var fields = type.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsImplicitlyDeclared).ToArray();
        if (fields.Any(field => !field.IsStatic && IsDisposable(field.Type)) && !IsDisposable(type))
            context.ReportDiagnostic(Diagnostic.Create(Disposal, location, type.Name));
        foreach (var field in fields.Where(field => field.IsStatic && !field.IsConst))
            context.ReportDiagnostic(Diagnostic.Create(StaticState, field.Locations.First(), field.Name));

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if ((method.IsOverride && method.Name is "OnAfterRender" or "OnAfterRenderAsync") ||
                method.ExplicitInterfaceImplementations.Any(implementation =>
                    implementation.ContainingType.ToDisplayString() == "Microsoft.AspNetCore.Components.IHandleAfterRender"))
                context.ReportDiagnostic(Diagnostic.Create(AfterRender, method.Locations.First(), method.Name));
        }

    }

    private static void AnalyzeDependency(SyntaxNodeAnalysisContext context)
    {
        var owner = context.ContainingSymbol as INamedTypeSymbol ?? context.ContainingSymbol?.ContainingType;
        if (owner is null || !IsMarked(owner)) return;
        if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is INamedTypeSymbol dependency &&
            IsBrowserDependency(dependency))
            context.ReportDiagnostic(Diagnostic.Create(Dependency, context.Node.GetLocation(), dependency.ToDisplayString()));
    }

    private static bool IsOrDerivesFrom(INamedTypeSymbol? type, string name)
    {
        for (; type is not null; type = type.BaseType)
            if (type.ToDisplayString() == name) return true;
        return false;
    }

    private static bool IsDisposable(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() is "System.IDisposable" or "System.IAsyncDisposable" ||
        type.AllInterfaces.Any(contract => contract.ToDisplayString() is "System.IDisposable" or "System.IAsyncDisposable");

    private static bool IsBrowserDependency(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var ns = current.ContainingNamespace.ToDisplayString();
            if (ns == "Microsoft.JSInterop" || ns.StartsWith("Microsoft.JSInterop.", StringComparison.Ordinal) ||
                ns == "Microsoft.AspNetCore.Http" || ns.StartsWith("Microsoft.AspNetCore.Http.", StringComparison.Ordinal) ||
                ns == "Microsoft.AspNetCore.Components.Server.Circuits" ||
                current.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() is "Microsoft.AspNetCore.Components.NavigationManager" or
                    "Microsoft.AspNetCore.Components.ElementReference" or
                    "Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider")
                return true;
        }
        return false;
    }

    private static void AnalyzeRenderMethod(IMethodSymbol method, Location location, CompilationAnalysisContext context,
        ConcurrentDictionary<IMethodSymbol, ImmutableArray<IOperation>> bodies,
        HashSet<IMethodSymbol> visited)
    {
        method = (method.PartialImplementationPart ?? method).OriginalDefinition;
        if (!visited.Add(method)) return;
        if (bodies.TryGetValue(method, out var operations))
        {
            foreach (var operation in operations) AnalyzeOperations(operation, context, bodies, visited);
        }
        else if (!(method.MethodKind == MethodKind.PropertyGet && method.DeclaringSyntaxReferences.Any(reference =>
                     reference.GetSyntax(context.CancellationToken) is AccessorDeclarationSyntax accessor &&
                     accessor.Body is null && accessor.ExpressionBody is null)))
        {
            context.ReportDiagnostic(Diagnostic.Create(RenderCall, location, method.ToDisplayString()));
        }
    }

    private static void AnalyzeOperations(IOperation operation, CompilationAnalysisContext context,
        ConcurrentDictionary<IMethodSymbol, ImmutableArray<IOperation>> bodies,
        HashSet<IMethodSymbol> visited)
    {
        IOperation? target = operation switch
        {
            IAssignmentOperation assignment => assignment.Target,
            IIncrementOrDecrementOperation increment => increment.Target,
            _ => null
        };
        if (target is not null && target is not ILocalReferenceOperation && target is not IDiscardOperation &&
            !(target is IParameterReferenceOperation parameter && parameter.Parameter.RefKind == RefKind.None))
            context.ReportDiagnostic(Diagnostic.Create(Mutation, operation.Syntax.GetLocation(), target.Syntax.ToString()));

        switch (operation)
        {
            case IInvocationOperation invocation:
                CheckCall(invocation.TargetMethod, invocation.Syntax.GetLocation(), context, bodies, visited);
                break;
            case IObjectCreationOperation creation when creation.Constructor is not null:
                CheckCall(creation.Constructor, creation.Syntax.GetLocation(), context, bodies, visited);
                break;
            case IPropertyReferenceOperation property when property.Property.GetMethod is not null:
                CheckCall(property.Property.GetMethod, property.Syntax.GetLocation(), context, bodies, visited);
                break;
            case IConversionOperation conversion when conversion.OperatorMethod is not null:
                CheckCall(conversion.OperatorMethod, conversion.Syntax.GetLocation(), context, bodies, visited);
                break;
            case IBinaryOperation binary when binary.OperatorMethod is not null:
                CheckCall(binary.OperatorMethod, binary.Syntax.GetLocation(), context, bodies, visited);
                break;
            case IUnaryOperation unary when unary.OperatorMethod is not null:
                CheckCall(unary.OperatorMethod, unary.Syntax.GetLocation(), context, bodies, visited);
                break;
            case IDynamicInvocationOperation:
            case IDynamicObjectCreationOperation:
            case IDynamicMemberReferenceOperation:
                context.ReportDiagnostic(Diagnostic.Create(RenderCall, operation.Syntax.GetLocation(), "dynamic"));
                break;
        }
        foreach (var child in operation.ChildOperations)
            AnalyzeOperations(child, context, bodies, visited);
    }

    private static void CheckCall(IMethodSymbol method, Location location, CompilationAnalysisContext context,
        ConcurrentDictionary<IMethodSymbol, ImmutableArray<IOperation>> bodies,
        HashSet<IMethodSymbol> visited)
    {
        if (IsSupportedRenderCall(method)) return;
        if (method.DeclaringSyntaxReferences.Length > 0 &&
            (!(method.IsVirtual || method.IsAbstract || method.IsOverride) || method.IsSealed || method.ContainingType.IsSealed))
            AnalyzeRenderMethod(method, location, context, bodies, visited);
        else
            context.ReportDiagnostic(Diagnostic.Create(RenderCall, location, method.ToDisplayString()));
    }

    private static bool IsSupportedRenderCall(IMethodSymbol method)
    {
        var owner = method.ContainingType.ToDisplayString();
        if (owner == "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder") return true;
        if (owner == "Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers" && method.Name == "TypeCheck") return true;
        if (owner == "string" && method.Name is "get_Length" or "ToString" or "IsNullOrEmpty" or "IsNullOrWhiteSpace") return true;
        if (owner == "System.Array" && method.Name == "get_Length") return true;
        if (method.MethodKind == MethodKind.PropertyGet && method.IsImplicitlyDeclared && method.ContainingType.IsRecord) return true;
        if (method.ContainingType.OriginalDefinition.ToDisplayString() == "Microsoft.AspNetCore.Components.CascadingValue<TValue>" &&
            method.Name is "get_Value" or "get_IsFixed") return true;
        if (method.ContainingType.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or
                SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
                SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
                SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal &&
            method.Name == "ToString") return true;
        if (owner is "System.Guid" or "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan")
            return method.Name == "ToString";
        return false;
    }
}
