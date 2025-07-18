﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp.UsePatternCombinators;

using static AnalyzedPattern;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class CSharpUsePatternCombinatorsDiagnosticAnalyzer() :
    AbstractBuiltInCodeStyleDiagnosticAnalyzer(
        IDEDiagnosticIds.UsePatternCombinatorsDiagnosticId,
        EnforceOnBuildValues.UsePatternCombinators,
        CSharpCodeStyleOptions.PreferPatternMatching,
        s_safePatternTitle,
        s_safePatternTitle)
{
    private const string SafeKey = "safe";

    private static readonly LocalizableResourceString s_safePatternTitle = new(nameof(CSharpAnalyzersResources.Use_pattern_matching), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources));
    private static readonly LocalizableResourceString s_unsafePatternTitle = new(nameof(CSharpAnalyzersResources.Use_pattern_matching_may_change_code_meaning), CSharpAnalyzersResources.ResourceManager, typeof(CSharpAnalyzersResources));

    private static readonly ImmutableDictionary<string, string?> s_safeProperties = ImmutableDictionary<string, string?>.Empty.Add(SafeKey, "");
    private static readonly DiagnosticDescriptor s_unsafeDescriptor = CreateDescriptorWithId(
        IDEDiagnosticIds.UsePatternCombinatorsDiagnosticId,
        EnforceOnBuildValues.UsePatternCombinators,
        hasAnyCodeStyleOption: true,
        s_unsafePatternTitle);

    public static bool IsSafe(Diagnostic diagnostic)
        => diagnostic.Properties.ContainsKey(SafeKey);

    protected override void InitializeWorker(AnalysisContext context)
        => context.RegisterSyntaxNodeAction(AnalyzeNode,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.LogicalNotExpression);

    private void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        var expression = (ExpressionSyntax)context.Node;
        if (expression.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return;

        // Bail if this is not a topmost expression
        // to avoid overlapping diagnostics.
        if (!IsTopmostExpression(expression))
            return;

        var syntaxTree = expression.SyntaxTree;
        if (syntaxTree.Options.LanguageVersion() < LanguageVersion.CSharp9)
            return;

        var cancellationToken = context.CancellationToken;
        var styleOption = context.GetCSharpAnalyzerOptions().PreferPatternMatching;
        if (!styleOption.Value || ShouldSkipAnalysis(context, styleOption.Notification))
            return;

        var semanticModel = context.SemanticModel;
        var expressionType = semanticModel.Compilation.ExpressionOfTType();
        if (expression.IsInExpressionTree(semanticModel, expressionType, cancellationToken))
            return;

        var operation = semanticModel.GetOperation(expression, cancellationToken);
        if (operation is null)
            return;

        var pattern = CSharpUsePatternCombinatorsAnalyzer.Analyze(operation);
        if (pattern is null)
            return;

        // Avoid rewriting trivial patterns, such as a single relational or a constant pattern.
        if (IsTrivial(pattern))
            return;

        // C# 9.0 does not support pattern variables under `not` and `or` combinators,
        // except for top-level `not` patterns.
        if (HasIllegalPatternVariables(pattern, isTopLevel: true))
            return;

        // if the target (the common expression in the pattern) is a method call,
        // then we can't guarantee that the rewritting won't have side-effects,
        // so we should warn the user
        var isSafe = pattern.Target.UnwrapImplicitConversion() is not Operations.IInvocationOperation;

        context.ReportDiagnostic(DiagnosticHelper.Create(
            descriptor: isSafe ? this.Descriptor : s_unsafeDescriptor,
            expression.GetLocation(),
            styleOption.Notification,
            context.Options,
            additionalLocations: null,
            properties: isSafe ? s_safeProperties : null));
    }

    private static bool HasIllegalPatternVariables(AnalyzedPattern pattern, bool permitDesignations = true, bool isTopLevel = false)
    {
        switch (pattern)
        {
            case Not p:
                return HasIllegalPatternVariables(p.Pattern, permitDesignations: isTopLevel);
            case Binary p:
                if (p.IsDisjunctive)
                    permitDesignations = false;
                return HasIllegalPatternVariables(p.Left, permitDesignations) ||
                       HasIllegalPatternVariables(p.Right, permitDesignations);
            case Source p when !permitDesignations:
                return p.PatternSyntax.DescendantNodes()
                    .OfType<SingleVariableDesignationSyntax>()
                    .Any(variable => !variable.Identifier.IsMissing);
            default:
                return false;
        }
    }

    private static bool IsTopmostExpression(ExpressionSyntax node)
    {
        return node.WalkUpParentheses().Parent switch
        {
            LambdaExpressionSyntax => true,
            AssignmentExpressionSyntax => true,
            ConditionalExpressionSyntax => true,
            ExpressionSyntax => false,
            _ => true
        };
    }

    private static bool IsTrivial(AnalyzedPattern pattern)
    {
        return pattern switch
        {
            Not { Pattern: Constant } => true,
            Not { Pattern: Source { PatternSyntax: ConstantPatternSyntax } } => true,
            Not => false,
            Binary => false,
            _ => true
        };
    }

    public override DiagnosticAnalyzerCategory GetAnalyzerCategory()
        => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;
}
