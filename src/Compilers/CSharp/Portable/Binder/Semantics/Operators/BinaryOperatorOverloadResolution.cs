﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class OverloadResolution
    {
        public void BinaryOperatorOverloadResolution(
            BinaryOperatorKind kind,
            bool isChecked,
            string name1,
            string name2Opt,
            BoundExpression left,
            BoundExpression right,
            BinaryOperatorOverloadResolutionResult result,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // We can do a table lookup for well-known problems in overload resolution.
            BinaryOperatorOverloadResolution_EasyOut(kind, left, right, result);
            if (result.Results.Count > 0)
            {
                return;
            }

            BinaryOperatorOverloadResolution_NoEasyOut(kind, isChecked, name1, name2Opt, left, right, result, ref useSiteInfo);
        }

        internal void BinaryOperatorOverloadResolution_EasyOut(BinaryOperatorKind kind, BoundExpression left, BoundExpression right, BinaryOperatorOverloadResolutionResult result)
        {
            Debug.Assert(left != null);
            Debug.Assert(right != null);
            Debug.Assert(result.Results.Count == 0);

            // SPEC: An operation of the form x&&y or x||y is processed by applying overload resolution
            // SPEC: as if the operation was written x&y or x|y.

            // SPEC VIOLATION: For compatibility with Dev11, do not apply this rule to built-in conversions.

            BinaryOperatorKind underlyingKind = kind & ~BinaryOperatorKind.Logical;

            BinaryOperatorEasyOut(underlyingKind, left, right, result);
        }

        internal void BinaryOperatorOverloadResolution_NoEasyOut(
            BinaryOperatorKind kind,
            bool isChecked,
            string name1,
            string name2Opt,
            BoundExpression left,
            BoundExpression right,
            BinaryOperatorOverloadResolutionResult result,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(left != null);
            Debug.Assert(right != null);
            Debug.Assert(result.Results.Count == 0);

            // The following is a slight rewording of the specification to emphasize that not all
            // operands of a binary operation need to have a type.

            // SPEC: An operation of the form x op y, where op is an overloadable binary operator is processed as follows:
            // SPEC: The set of candidate user-defined operators provided by the types (if any) of x and y for the 
            // SPEC operation operator op(x, y) is determined. 

            TypeSymbol leftOperatorSourceOpt = left.Type?.StrippedType();
            TypeSymbol rightOperatorSourceOpt = right.Type?.StrippedType();
            bool leftSourceIsInterface = leftOperatorSourceOpt?.IsInterfaceType() == true;
            bool rightSourceIsInterface = rightOperatorSourceOpt?.IsInterfaceType() == true;

            // The following is a slight rewording of the specification to emphasize that not all
            // operands of a binary operation need to have a type.

            // TODO (tomat): The spec needs to be updated to use identity conversion instead of type equality.

            // Spec 7.3.4 Binary operator overload resolution:
            //   An operation of the form x op y, where op is an overloadable binary operator is processed as follows:
            //   The set of candidate user-defined operators provided by the types (if any) of x and y for the 
            //   operation operator op(x, y) is determined. The set consists of the union of the candidate operators
            //   provided by the type of x (if any) and the candidate operators provided by the type of y (if any), 
            //   each determined using the rules of 7.3.5. Candidate operators only occur in the combined set once.

            // From https://github.com/dotnet/csharplang/blob/main/meetings/2017/LDM-2017-06-27.md:
            // - We only even look for operator implementations in interfaces if one of the operands has a type that is
            // an interface or a type parameter with a non-empty effective base interface list.
            // - We should look at operators from classes first, in order to avoid breaking changes.
            // Only if there are no applicable user-defined operators from classes will we look in interfaces.
            // If there aren't any there either, we go to built-ins.
            // - If we find an applicable candidate in an interface, that candidate shadows all applicable operators in
            // base interfaces: we stop looking.

            bool hadApplicableCandidates = false;

            // In order to preserve backward compatibility, at first we ignore interface sources.

            if ((object)leftOperatorSourceOpt != null && !leftSourceIsInterface)
            {
                hadApplicableCandidates = GetUserDefinedOperators(kind, isChecked, name1, name2Opt, leftOperatorSourceOpt, left, right, result.Results, ref useSiteInfo);
                if (!hadApplicableCandidates)
                {
                    result.Results.Clear();
                }
            }

            bool isShift = kind.IsShift();

            if (!isShift && (object)rightOperatorSourceOpt != null && !rightSourceIsInterface && !rightOperatorSourceOpt.Equals(leftOperatorSourceOpt))
            {
                var rightOperators = ArrayBuilder<BinaryOperatorAnalysisResult>.GetInstance();
                if (GetUserDefinedOperators(kind, isChecked, name1, name2Opt, rightOperatorSourceOpt, left, right, rightOperators, ref useSiteInfo))
                {
                    hadApplicableCandidates = true;
                    AddDistinctOperators(result.Results, rightOperators);
                }

                rightOperators.Free();
            }

            Debug.Assert((result.Results.Count == 0) != hadApplicableCandidates);

            // If there are no applicable candidates in classes / stuctures, try with interface sources.
            if (!hadApplicableCandidates)
            {
                result.Results.Clear();

                var lookedInInterfaces = PooledDictionary<TypeSymbol, bool>.GetInstance();

                TypeSymbol firstOperatorSourceOpt;
                TypeSymbol secondOperatorSourceOpt;
                bool firstSourceIsInterface;
                bool secondSourceIsInterface;

                // Always start lookup from a type parameter. This ensures that regardless of the order we always pick up constrained type for
                // each distinct candidate operator.
                if (!isShift && (leftOperatorSourceOpt is null || (leftOperatorSourceOpt is not TypeParameterSymbol && rightOperatorSourceOpt is TypeParameterSymbol)))
                {
                    firstOperatorSourceOpt = rightOperatorSourceOpt;
                    secondOperatorSourceOpt = leftOperatorSourceOpt;
                    firstSourceIsInterface = rightSourceIsInterface;
                    secondSourceIsInterface = leftSourceIsInterface;
                }
                else
                {
                    firstOperatorSourceOpt = leftOperatorSourceOpt;
                    secondOperatorSourceOpt = rightOperatorSourceOpt;
                    firstSourceIsInterface = leftSourceIsInterface;
                    secondSourceIsInterface = rightSourceIsInterface;
                }

                hadApplicableCandidates = GetUserDefinedBinaryOperatorsFromInterfaces(kind, isChecked, name1, name2Opt,
                        firstOperatorSourceOpt, firstSourceIsInterface, left, right, ref useSiteInfo, lookedInInterfaces, result.Results);
                if (!hadApplicableCandidates)
                {
                    result.Results.Clear();
                }

                if (!isShift && (object)secondOperatorSourceOpt != null && !secondOperatorSourceOpt.Equals(firstOperatorSourceOpt))
                {
                    var rightOperators = ArrayBuilder<BinaryOperatorAnalysisResult>.GetInstance();
                    if (GetUserDefinedBinaryOperatorsFromInterfaces(kind, isChecked, name1, name2Opt,
                            secondOperatorSourceOpt, secondSourceIsInterface, left, right, ref useSiteInfo, lookedInInterfaces, rightOperators))
                    {
                        hadApplicableCandidates = true;
                        AddDistinctOperators(result.Results, rightOperators);
                    }

                    rightOperators.Free();
                }

                lookedInInterfaces.Free();
            }

            // SPEC: If the set of candidate user-defined operators is not empty, then this becomes the set of candidate 
            // SPEC: operators for the operation. Otherwise, the predefined binary operator op implementations, including 
            // SPEC: their lifted forms, become the set of candidate operators for the operation. 

            // Note that the native compiler has a bug in its binary operator overload resolution involving 
            // lifted built-in operators.  The spec says that we should add the lifted and unlifted operators
            // to a candidate set, eliminate the inapplicable operators, and then choose the best of what is left.
            // The lifted operator is defined as, say int? + int? --> int?.  That is not what the native compiler
            // does. The native compiler, rather, effectively says that there are *three* lifted operators:
            // int? + int? --> int?, int + int? --> int? and int? + int --> int?, and it chooses the best operator
            // amongst those choices.  
            //
            // This is a subtle difference; most of the time all it means is that we generate better code because we
            // skip an unnecessary operand conversion to int? when adding int to int?. But some of the time it
            // means that a different user-defined conversion is chosen than the one you would expect, if the
            // operand has a user-defined conversion to both int and int?.
            //
            // Roslyn matches the specification and takes the break from the native compiler.

            Debug.Assert((result.Results.Count == 0) != hadApplicableCandidates);

            if (!hadApplicableCandidates)
            {
                result.Results.Clear();
                GetAllBuiltInOperators(kind, isChecked, left, right, result.Results, ref useSiteInfo);
            }

            // SPEC: The overload resolution rules of 7.5.3 are applied to the set of candidate operators to select the best 
            // SPEC: operator with respect to the argument list (x, y), and this operator becomes the result of the overload 
            // SPEC: resolution process. If overload resolution fails to select a single best operator, a binding-time 
            // SPEC: error occurs.

            BinaryOperatorOverloadResolution(left, right, result, ref useSiteInfo);
        }

        private bool GetUserDefinedBinaryOperatorsFromInterfaces(BinaryOperatorKind kind, bool isChecked,
            string name1,
            string name2Opt,
            TypeSymbol operatorSourceOpt, bool sourceIsInterface,
            BoundExpression left, BoundExpression right, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            Dictionary<TypeSymbol, bool> lookedInInterfaces, ArrayBuilder<BinaryOperatorAnalysisResult> candidates)
        {
            Debug.Assert(candidates.Count == 0);

            if ((object)operatorSourceOpt == null)
            {
                return false;
            }

            bool hadUserDefinedCandidateFromInterfaces = false;
            ImmutableArray<NamedTypeSymbol> interfaces = default;
            TypeSymbol constrainedToTypeOpt = null;

            if (sourceIsInterface)
            {

                if (!lookedInInterfaces.TryGetValue(operatorSourceOpt, out _))
                {
                    var operators = ArrayBuilder<BinaryOperatorSignature>.GetInstance();
                    GetUserDefinedBinaryOperatorsFromType(constrainedToTypeOpt, (NamedTypeSymbol)operatorSourceOpt, kind, name1, name2Opt, operators);
                    hadUserDefinedCandidateFromInterfaces = CandidateOperators(isChecked, operators, left, right, candidates, ref useSiteInfo);
                    operators.Free();
                    Debug.Assert(hadUserDefinedCandidateFromInterfaces == candidates.Any(r => r.IsValid));

                    lookedInInterfaces.Add(operatorSourceOpt, hadUserDefinedCandidateFromInterfaces);
                    if (!hadUserDefinedCandidateFromInterfaces)
                    {
                        candidates.Clear();
                        interfaces = operatorSourceOpt.AllInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
                    }
                }
            }
            else if (operatorSourceOpt.IsTypeParameter())
            {
                interfaces = ((TypeParameterSymbol)operatorSourceOpt).AllEffectiveInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
                constrainedToTypeOpt = operatorSourceOpt;
            }

            if (!interfaces.IsDefaultOrEmpty)
            {
                var operators = ArrayBuilder<BinaryOperatorSignature>.GetInstance();
                var results = ArrayBuilder<BinaryOperatorAnalysisResult>.GetInstance();
                var shadowedInterfaces = PooledHashSet<NamedTypeSymbol>.GetInstance();

                foreach (NamedTypeSymbol @interface in interfaces)
                {
                    if (!@interface.IsInterface)
                    {
                        // this code could be reachable in error situations
                        continue;
                    }

                    if (shadowedInterfaces.Contains(@interface))
                    {
                        // this interface is "shadowed" by a derived interface
                        continue;
                    }

                    if (lookedInInterfaces.TryGetValue(@interface, out bool hadUserDefinedCandidate))
                    {
                        if (hadUserDefinedCandidate)
                        {
                            // this interface "shadows" all its base interfaces
                            shadowedInterfaces.AddAll(@interface.AllInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo));
                        }

                        // no need to perform another lookup in this interface
                        continue;
                    }

                    operators.Clear();
                    results.Clear();
                    GetUserDefinedBinaryOperatorsFromType(constrainedToTypeOpt, @interface, kind, name1, name2Opt, operators);
                    hadUserDefinedCandidate = CandidateOperators(isChecked, operators, left, right, results, ref useSiteInfo);
                    Debug.Assert(hadUserDefinedCandidate == results.Any(r => r.IsValid));
                    lookedInInterfaces.Add(@interface, hadUserDefinedCandidate);
                    if (hadUserDefinedCandidate)
                    {
                        hadUserDefinedCandidateFromInterfaces = true;
                        candidates.AddRange(results);
                        // this interface "shadows" all its base interfaces
                        shadowedInterfaces.AddAll(@interface.AllInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo));
                    }
                }

                operators.Free();
                results.Free();
                shadowedInterfaces.Free();
            }

            return hadUserDefinedCandidateFromInterfaces;
        }

        private void AddDelegateOperation(BinaryOperatorKind kind, TypeSymbol delegateType,
            ArrayBuilder<BinaryOperatorSignature> operators)
        {
            switch (kind)
            {
                case BinaryOperatorKind.Equal:
                case BinaryOperatorKind.NotEqual:
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Delegate, delegateType, delegateType, Compilation.GetSpecialType(SpecialType.System_Boolean)));
                    break;

                case BinaryOperatorKind.Addition:
                case BinaryOperatorKind.Subtraction:
                default:
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Delegate, delegateType, delegateType, delegateType));
                    break;
            }
        }

        private void GetDelegateOperations(BinaryOperatorKind kind, BoundExpression left, BoundExpression right,
            ArrayBuilder<BinaryOperatorSignature> operators, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(left != null);
            Debug.Assert(right != null);
            AssertNotChecked(kind);

            switch (kind)
            {
                case BinaryOperatorKind.Multiplication:
                case BinaryOperatorKind.Division:
                case BinaryOperatorKind.Remainder:
                case BinaryOperatorKind.RightShift:
                case BinaryOperatorKind.UnsignedRightShift:
                case BinaryOperatorKind.LeftShift:
                case BinaryOperatorKind.And:
                case BinaryOperatorKind.Or:
                case BinaryOperatorKind.Xor:
                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                case BinaryOperatorKind.LessThanOrEqual:
                case BinaryOperatorKind.LogicalAnd:
                case BinaryOperatorKind.LogicalOr:
                    return;

                case BinaryOperatorKind.Addition:
                case BinaryOperatorKind.Subtraction:
                case BinaryOperatorKind.Equal:
                case BinaryOperatorKind.NotEqual:
                    break;

                default:
                    // Unhandled bin op kind in get delegate operation
                    throw ExceptionUtilities.UnexpectedValue(kind);
            }

            var leftType = left.Type;
            var leftDelegate = (object)leftType != null && leftType.IsDelegateType();
            var rightType = right.Type;
            var rightDelegate = (object)rightType != null && rightType.IsDelegateType();

            // If no operands have delegate types then add nothing.
            if (!leftDelegate && !rightDelegate)
            {
                // Even though neither left nor right type is a delegate type,
                // both types might have implicit conversions to System.Delegate type.

                // Spec 7.10.8: Delegate equality operators:
                // Every delegate type implicitly provides the following predefined comparison operators:
                //     bool operator ==(System.Delegate x, System.Delegate y)
                //     bool operator !=(System.Delegate x, System.Delegate y)

                switch (OperatorKindExtensions.Operator(kind))
                {
                    case BinaryOperatorKind.Equal:
                    case BinaryOperatorKind.NotEqual:
                        TypeSymbol systemDelegateType = _binder.Compilation.GetSpecialType(SpecialType.System_Delegate);
                        systemDelegateType.AddUseSiteInfo(ref useSiteInfo);

                        if (Conversions.ClassifyImplicitConversionFromExpression(left, systemDelegateType, ref useSiteInfo).IsValid &&
                            Conversions.ClassifyImplicitConversionFromExpression(right, systemDelegateType, ref useSiteInfo).IsValid)
                        {
                            AddDelegateOperation(kind, systemDelegateType, operators);
                        }

                        break;
                }

                return;
            }

            // We might have a situation like
            //
            // Func<string> + Func<object>
            // 
            // in which case overload resolution should consider both 
            //
            // Func<string> + Func<string>
            // Func<object> + Func<object>
            //
            // are candidates (and it will pick Func<object>). Similarly,
            // we might have something like:
            //
            // Func<object> + Func<dynamic>
            // 
            // in which case neither candidate is better than the other,
            // resulting in an error.
            //
            // We could as an optimization say that if you are adding two completely
            // dissimilar delegate types D1 and D2, that neither is added to the candidate
            // set because neither can possibly be applicable, but let's not go there.
            // Let's just add them to the set and let overload resolution (and the 
            // error recovery heuristics) have at the real candidate set.
            //
            // However, we will take a spec violation for this scenario:
            //
            // SPEC VIOLATION:
            //
            // Technically the spec implies that we ought to be able to compare 
            // 
            // Func<int> x = whatever;
            // bool y = x == ()=>1;
            //
            // The native compiler does not allow this. I see no
            // reason why we ought to allow this. However, a good question is whether
            // the violation ought to be here, where we are determining the operator
            // candidate set, or in overload resolution where we are determining applicability.
            // In the native compiler we did it during candidate set determination, 
            // so let's stick with that.

            if (leftDelegate && rightDelegate)
            {
                // They are both delegate types. Add them both if they are different types.
                AddDelegateOperation(kind, leftType, operators);

                // There is no reason why we can't compare instances of delegate types that are identity convertible.
                // We can't perform + or - operation on them since it is not clear what the return type of such operation should be.
                bool useIdentityConversion = kind == BinaryOperatorKind.Equal || kind == BinaryOperatorKind.NotEqual;

                if (!(useIdentityConversion ? Conversions.HasIdentityConversion(leftType, rightType) : leftType.Equals(rightType)))
                {
                    AddDelegateOperation(kind, rightType, operators);
                }

                return;
            }

            // One of them is a delegate, the other is not.
            TypeSymbol delegateType = leftDelegate ? leftType : rightType;
            BoundExpression nonDelegate = leftDelegate ? right : left;

            if ((kind == BinaryOperatorKind.Equal || kind == BinaryOperatorKind.NotEqual)
                && nonDelegate.Kind == BoundKind.UnboundLambda)
            {
                return;
            }

            AddDelegateOperation(kind, delegateType, operators);
        }

        private void GetEnumOperation(BinaryOperatorKind kind, TypeSymbol enumType, BoundExpression right, ArrayBuilder<BinaryOperatorSignature> operators)
        {
            Debug.Assert((object)enumType != null);
            AssertNotChecked(kind);

            if (!enumType.IsValidEnumType())
            {
                return;
            }

            var underlying = enumType.GetEnumUnderlyingType();
            Debug.Assert((object)underlying != null);
            Debug.Assert(underlying.SpecialType != SpecialType.None);

            var nullableEnum = Compilation.GetOrCreateNullableType(enumType);
            var nullableUnderlying = Compilation.GetOrCreateNullableType(underlying);

            switch (kind)
            {
                case BinaryOperatorKind.Addition:
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.EnumAndUnderlyingAddition, enumType, underlying, enumType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.UnderlyingAndEnumAddition, underlying, enumType, enumType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedEnumAndUnderlyingAddition, nullableEnum, nullableUnderlying, nullableEnum));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedUnderlyingAndEnumAddition, nullableUnderlying, nullableEnum, nullableEnum));
                    break;
                case BinaryOperatorKind.Subtraction:
                    if (Strict)
                    {
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.EnumSubtraction, enumType, enumType, underlying));
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.EnumAndUnderlyingSubtraction, enumType, underlying, enumType));
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedEnumSubtraction, nullableEnum, nullableEnum, nullableUnderlying));
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedEnumAndUnderlyingSubtraction, nullableEnum, nullableUnderlying, nullableEnum));
                    }
                    else
                    {
                        // SPEC VIOLATION:
                        // The native compiler has bugs in overload resolution involving binary operator- for enums,
                        // which we duplicate by hardcoding Priority values among the operators. When present on both
                        // methods being compared during overload resolution, Priority values are used to decide between
                        // two candidates (instead of the usual language-specified rules).
                        bool isExactSubtraction = TypeSymbol.Equals(right.Type?.StrippedType(), underlying, TypeCompareKind.ConsiderEverything2);
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.EnumSubtraction, enumType, enumType, underlying)
                        { Priority = 2 });
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.EnumAndUnderlyingSubtraction, enumType, underlying, enumType)
                        { Priority = isExactSubtraction ? 1 : 3 });
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedEnumSubtraction, nullableEnum, nullableEnum, nullableUnderlying)
                        { Priority = 12 });
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedEnumAndUnderlyingSubtraction, nullableEnum, nullableUnderlying, nullableEnum)
                        { Priority = isExactSubtraction ? 11 : 13 });

                        // Due to a bug, the native compiler allows "underlying - enum", so Roslyn does as well.
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.UnderlyingAndEnumSubtraction, underlying, enumType, enumType)
                        { Priority = 4 });
                        operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LiftedUnderlyingAndEnumSubtraction, nullableUnderlying, nullableEnum, nullableEnum)
                        { Priority = 14 });
                    }
                    break;
                case BinaryOperatorKind.Equal:
                case BinaryOperatorKind.NotEqual:
                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                case BinaryOperatorKind.LessThanOrEqual:
                    var boolean = Compilation.GetSpecialType(SpecialType.System_Boolean);
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Enum, enumType, enumType, boolean));
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Lifted | BinaryOperatorKind.Enum, nullableEnum, nullableEnum, boolean));
                    break;
                case BinaryOperatorKind.And:
                case BinaryOperatorKind.Or:
                case BinaryOperatorKind.Xor:
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Enum, enumType, enumType, enumType));
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Lifted | BinaryOperatorKind.Enum, nullableEnum, nullableEnum, nullableEnum));
                    break;
            }
        }

        private void GetPointerArithmeticOperators(
            BinaryOperatorKind kind,
            PointerTypeSymbol pointerType,
            ArrayBuilder<BinaryOperatorSignature> operators)
        {
            Debug.Assert((object)pointerType != null);
            AssertNotChecked(kind);

            switch (kind)
            {
                case BinaryOperatorKind.Addition:
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndIntAddition, pointerType, Compilation.GetSpecialType(SpecialType.System_Int32), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndUIntAddition, pointerType, Compilation.GetSpecialType(SpecialType.System_UInt32), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndLongAddition, pointerType, Compilation.GetSpecialType(SpecialType.System_Int64), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndULongAddition, pointerType, Compilation.GetSpecialType(SpecialType.System_UInt64), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.IntAndPointerAddition, Compilation.GetSpecialType(SpecialType.System_Int32), pointerType, pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.UIntAndPointerAddition, Compilation.GetSpecialType(SpecialType.System_UInt32), pointerType, pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.LongAndPointerAddition, Compilation.GetSpecialType(SpecialType.System_Int64), pointerType, pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.ULongAndPointerAddition, Compilation.GetSpecialType(SpecialType.System_UInt64), pointerType, pointerType));
                    break;
                case BinaryOperatorKind.Subtraction:
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndIntSubtraction, pointerType, Compilation.GetSpecialType(SpecialType.System_Int32), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndUIntSubtraction, pointerType, Compilation.GetSpecialType(SpecialType.System_UInt32), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndLongSubtraction, pointerType, Compilation.GetSpecialType(SpecialType.System_Int64), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerAndULongSubtraction, pointerType, Compilation.GetSpecialType(SpecialType.System_UInt64), pointerType));
                    operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.PointerSubtraction, pointerType, pointerType, Compilation.GetSpecialType(SpecialType.System_Int64)));
                    break;
            }
        }

        private void GetPointerComparisonOperators(
            BinaryOperatorKind kind,
            ArrayBuilder<BinaryOperatorSignature> operators)
        {
            switch (kind)
            {
                case BinaryOperatorKind.Equal:
                case BinaryOperatorKind.NotEqual:
                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                case BinaryOperatorKind.LessThanOrEqual:
                    var voidPointerType = new PointerTypeSymbol(TypeWithAnnotations.Create(Compilation.GetSpecialType(SpecialType.System_Void)));
                    operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Pointer, voidPointerType, voidPointerType, Compilation.GetSpecialType(SpecialType.System_Boolean)));
                    break;
            }
        }

        private void GetEnumOperations(BinaryOperatorKind kind, BoundExpression left, BoundExpression right, ArrayBuilder<BinaryOperatorSignature> results)
        {
            Debug.Assert(left != null);
            Debug.Assert(right != null);
            AssertNotChecked(kind);

            // First take some easy outs:
            switch (kind)
            {
                case BinaryOperatorKind.Multiplication:
                case BinaryOperatorKind.Division:
                case BinaryOperatorKind.Remainder:
                case BinaryOperatorKind.RightShift:
                case BinaryOperatorKind.UnsignedRightShift:
                case BinaryOperatorKind.LeftShift:
                case BinaryOperatorKind.LogicalAnd:
                case BinaryOperatorKind.LogicalOr:
                    return;
            }

            var leftType = left.Type;
            if ((object)leftType != null)
            {
                leftType = leftType.StrippedType();
            }

            var rightType = right.Type;
            if ((object)rightType != null)
            {
                rightType = rightType.StrippedType();
            }

            bool useIdentityConversion;
            switch (kind)
            {
                case BinaryOperatorKind.And:
                case BinaryOperatorKind.Or:
                case BinaryOperatorKind.Xor:
                    // These operations are ambiguous on non-equal identity-convertible types - 
                    // it's not clear what the resulting type of the operation should be:
                    //   C<?>.E operator +(C<dynamic>.E x, C<object>.E y)
                    useIdentityConversion = false;
                    break;

                case BinaryOperatorKind.Addition:
                    // Addition only accepts a single enum type, so operations on non-equal identity-convertible types are not ambiguous. 
                    //   E operator +(E x, U y)
                    //   E operator +(U x, E y)
                    useIdentityConversion = true;
                    break;

                case BinaryOperatorKind.Subtraction:
                    // Subtraction either returns underlying type or only accept a single enum type, so operations on non-equal identity-convertible types are not ambiguous. 
                    //   U operator –(E x, E y)
                    //   E operator –(E x, U y)
                    useIdentityConversion = true;
                    break;

                case BinaryOperatorKind.Equal:
                case BinaryOperatorKind.NotEqual:
                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                case BinaryOperatorKind.LessThanOrEqual:
                    // Relational operations return Boolean, so operations on non-equal identity-convertible types are not ambiguous. 
                    //   Boolean operator op(C<dynamic>.E, C<object>.E)
                    useIdentityConversion = true;
                    break;

                default:
                    // Unhandled bin op kind in get enum operations
                    throw ExceptionUtilities.UnexpectedValue(kind);
            }

            if ((object)leftType != null)
            {
                GetEnumOperation(kind, leftType, right, results);
            }

            if ((object)rightType != null && ((object)leftType == null || !(useIdentityConversion ? Conversions.HasIdentityConversion(rightType, leftType) : rightType.Equals(leftType))))
            {
                GetEnumOperation(kind, rightType, right, results);
            }
        }

        private void GetPointerOperators(
            BinaryOperatorKind kind,
            BoundExpression left,
            BoundExpression right,
            ArrayBuilder<BinaryOperatorSignature> results)
        {
            Debug.Assert(left != null);
            Debug.Assert(right != null);
            AssertNotChecked(kind);

            var leftType = left.Type as PointerTypeSymbol;
            var rightType = right.Type as PointerTypeSymbol;

            if ((object)leftType != null)
            {
                GetPointerArithmeticOperators(kind, leftType, results);
            }

            // The only arithmetic operator that is applicable on two distinct pointer types is
            //   long operator –(T* x, T* y)
            // This operator returns long and so it's not ambiguous to apply it on T1 and T2 that are identity convertible to each other.
            if ((object)rightType != null && ((object)leftType == null || !Conversions.HasIdentityConversion(rightType, leftType)))
            {
                GetPointerArithmeticOperators(kind, rightType, results);
            }

            if ((object)leftType != null || (object)rightType != null || left.Type is FunctionPointerTypeSymbol || right.Type is FunctionPointerTypeSymbol)
            {
                // The pointer comparison operators are all "void* OP void*".
                GetPointerComparisonOperators(kind, results);
            }
        }

        private void GetAllBuiltInOperators(BinaryOperatorKind kind, bool isChecked, BoundExpression left, BoundExpression right, ArrayBuilder<BinaryOperatorAnalysisResult> results, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // Strip the "checked" off; the checked-ness of the context does not affect which built-in operators
            // are applicable.
            kind = kind.OperatorWithLogical();
            var operators = ArrayBuilder<BinaryOperatorSignature>.GetInstance();
            bool isEquality = kind == BinaryOperatorKind.Equal || kind == BinaryOperatorKind.NotEqual;
            if (isEquality && useOnlyReferenceEquality(Conversions, left, right, ref useSiteInfo))
            {
                // As a special case, if the reference equality operator is applicable (and it
                // is not a string or delegate) we do not check any other operators.  This patches
                // what is otherwise a flaw in the language specification.  See 11426.
                GetReferenceEquality(kind, operators);
                Debug.Assert(operators.Count == 1);

                if ((left.Type is TypeParameterSymbol { AllowsRefLikeType: true } && right.IsLiteralNull()) ||
                    (right.Type is TypeParameterSymbol { AllowsRefLikeType: true } && left.IsLiteralNull()))
                {
                    BinaryOperatorSignature op = operators[0];
                    Debug.Assert(op.LeftType.IsObjectType());
                    Debug.Assert(op.RightType.IsObjectType());

                    var convLeft = getOperandConversionForAllowByRefLikeNullCheck(isChecked, left, op.LeftType, ref useSiteInfo);
                    var convRight = getOperandConversionForAllowByRefLikeNullCheck(isChecked, right, op.RightType, ref useSiteInfo);

                    Debug.Assert(convLeft.IsImplicit);
                    Debug.Assert(convRight.IsImplicit);

                    results.Add(BinaryOperatorAnalysisResult.Applicable(op, convLeft, convRight));
                    operators.Free();
                    return;
                }
            }
            else
            {
                this.Compilation.BuiltInOperators.GetSimpleBuiltInOperators(kind, operators, skipNativeIntegerOperators: !left.Type.IsNativeIntegerOrNullableThereof() && !right.Type.IsNativeIntegerOrNullableThereof());

                // SPEC 7.3.4: For predefined enum and delegate operators, the only operators
                // considered are those defined by an enum or delegate type that is the binding
                //-time type of one of the operands.
                GetDelegateOperations(kind, left, right, operators, ref useSiteInfo);
                GetEnumOperations(kind, left, right, operators);

                // We similarly limit pointer operator candidates considered.
                GetPointerOperators(kind, left, right, operators);

                if (kind.Operator() is BinaryOperatorKind.Addition &&
                    isUtf8ByteRepresentation(left) &&
                    isUtf8ByteRepresentation(right))
                {
                    this.Compilation.BuiltInOperators.GetUtf8ConcatenationBuiltInOperator(left.Type, operators);
                }
            }

            CandidateOperators(isChecked, operators, left, right, results, ref useSiteInfo);
            operators.Free();

            static bool useOnlyReferenceEquality(Conversions conversions, BoundExpression left, BoundExpression right, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
            {
                // We consider the `null` literal, but not the `default` literal, since the latter does not require a reference equality
                return
                    BuiltInOperators.IsValidObjectEquality(conversions, left.Type, left.IsLiteralNull(), leftIsDefault: false, right.Type, right.IsLiteralNull(), rightIsDefault: false, ref useSiteInfo) &&
                    ((object)left.Type == null || (!left.Type.IsDelegateType() && left.Type.SpecialType != SpecialType.System_String && left.Type.SpecialType != SpecialType.System_Delegate)) &&
                    ((object)right.Type == null || (!right.Type.IsDelegateType() && right.Type.SpecialType != SpecialType.System_String && right.Type.SpecialType != SpecialType.System_Delegate));
            }

            static bool isUtf8ByteRepresentation(BoundExpression value)
            {
                return value is BoundUtf8String or BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Utf8Addition };
            }

            Conversion getOperandConversionForAllowByRefLikeNullCheck(bool isChecked, BoundExpression operand, TypeSymbol objectType, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
            {
                return (operand.Type is TypeParameterSymbol { AllowsRefLikeType: true }) ? Conversion.Boxing : Conversions.ClassifyConversionFromExpression(operand, objectType, isChecked: isChecked, ref useSiteInfo);
            }
        }

        private void GetReferenceEquality(BinaryOperatorKind kind, ArrayBuilder<BinaryOperatorSignature> operators)
        {
            var @object = Compilation.GetSpecialType(SpecialType.System_Object);
            operators.Add(new BinaryOperatorSignature(kind | BinaryOperatorKind.Object, @object, @object, Compilation.GetSpecialType(SpecialType.System_Boolean)));
        }

        private bool CandidateOperators(
            bool isChecked,
            ArrayBuilder<BinaryOperatorSignature> operators,
            BoundExpression left,
            BoundExpression right,
            ArrayBuilder<BinaryOperatorAnalysisResult> results,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            bool hadApplicableCandidate = false;
            foreach (var op in operators)
            {
                var convLeft = Conversions.ClassifyConversionFromExpression(left, op.LeftType, isChecked: isChecked, ref useSiteInfo);
                var convRight = Conversions.ClassifyConversionFromExpression(right, op.RightType, isChecked: isChecked, ref useSiteInfo);
                if (convLeft.IsImplicit && convRight.IsImplicit)
                {
                    results.Add(BinaryOperatorAnalysisResult.Applicable(op, convLeft, convRight));
                    hadApplicableCandidate = true;
                }
                else
                {
                    results.Add(BinaryOperatorAnalysisResult.Inapplicable(op, convLeft, convRight));
                }
            }
            return hadApplicableCandidate;
        }

        private static void AddDistinctOperators(ArrayBuilder<BinaryOperatorAnalysisResult> result, ArrayBuilder<BinaryOperatorAnalysisResult> additionalOperators)
        {
            int initialCount = result.Count;

            foreach (var op in additionalOperators)
            {
                bool equivalentToExisting = false;

                for (int i = 0; i < initialCount; i++)
                {
                    var existingSignature = result[i].Signature;

                    Debug.Assert(op.Signature.Kind.Operator() == existingSignature.Kind.Operator());

                    // Return types must match exactly, parameters might match modulo identity conversion.
                    if (op.Signature.Kind == existingSignature.Kind && // Easy out
                        equalsIgnoringNullable(op.Signature.ReturnType, existingSignature.ReturnType) &&
                        equalsIgnoringNullableAndDynamic(op.Signature.LeftType, existingSignature.LeftType) &&
                        equalsIgnoringNullableAndDynamic(op.Signature.RightType, existingSignature.RightType) &&
                        equalsIgnoringNullableAndDynamic(op.Signature.Method.ContainingType, existingSignature.Method.ContainingType))
                    {
                        equivalentToExisting = true;
                        break;
                    }
                }

                if (!equivalentToExisting)
                {
                    result.Add(op);
                }
            }

            static bool equalsIgnoringNullable(TypeSymbol a, TypeSymbol b) => a.Equals(b, TypeCompareKind.AllNullableIgnoreOptions);
            static bool equalsIgnoringNullableAndDynamic(TypeSymbol a, TypeSymbol b) => a.Equals(b, TypeCompareKind.AllNullableIgnoreOptions | TypeCompareKind.IgnoreDynamic);
        }

        private bool GetUserDefinedOperators(
            BinaryOperatorKind kind,
            bool isChecked,
            string name1,
            string name2Opt,
            TypeSymbol type0,
            BoundExpression left,
            BoundExpression right,
            ArrayBuilder<BinaryOperatorAnalysisResult> results,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(results.Count == 0);
            if ((object)type0 == null || OperatorFacts.DefinitelyHasNoUserDefinedOperators(type0))
            {
                return false;
            }

            // Spec 7.3.5 Candidate user-defined operators
            // SPEC: Given a type T and an operation operator op(A), where op is an overloadable 
            // SPEC: operator and A is an argument list, the set of candidate user-defined operators 
            // SPEC: provided by T for operator op(A) is determined as follows:

            // SPEC: Determine the type T0. If T is a nullable type, T0 is its underlying type, 
            // SPEC: otherwise T0 is equal to T.

            // (The caller has already passed in the stripped type.)

            // SPEC: For all operator op declarations in T0 and all lifted forms of such operators, 
            // SPEC: if at least one operator is applicable (7.5.3.1) with respect to the argument 
            // SPEC: list A, then the set of candidate operators consists of all such applicable 
            // SPEC: operators in T0. Otherwise, if T0 is object, the set of candidate operators is empty.
            // SPEC: Otherwise, the set of candidate operators provided by T0 is the set of candidate 
            // SPEC: operators provided by the direct base class of T0, or the effective base class of
            // SPEC: T0 if T0 is a type parameter.

            var operators = ArrayBuilder<BinaryOperatorSignature>.GetInstance();
            bool hadApplicableCandidates = false;

            NamedTypeSymbol current = type0 as NamedTypeSymbol;
            if ((object)current == null)
            {
                current = type0.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
            }

            if ((object)current == null && type0.IsTypeParameter())
            {
                current = ((TypeParameterSymbol)type0).EffectiveBaseClass(ref useSiteInfo);
            }

            for (; (object)current != null; current = current.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo))
            {
                operators.Clear();
                GetUserDefinedBinaryOperatorsFromType(constrainedToTypeOpt: null, current, kind, name1, name2Opt, operators);
                results.Clear();
                if (CandidateOperators(isChecked, operators, left, right, results, ref useSiteInfo))
                {
                    hadApplicableCandidates = true;
                    break;
                }
            }

            operators.Free();

            Debug.Assert(hadApplicableCandidates == results.Any(r => r.IsValid));
            return hadApplicableCandidates;
        }

#nullable enable

        internal static void GetStaticUserDefinedBinaryOperatorMethodNames(BinaryOperatorKind kind, bool isChecked, out string name1, out string? name2Opt)
        {
            name1 = OperatorFacts.BinaryOperatorNameFromOperatorKind(kind, isChecked);

            if (isChecked && SyntaxFacts.IsCheckedOperator(name1))
            {
                name2Opt = OperatorFacts.BinaryOperatorNameFromOperatorKind(kind, isChecked: false);
            }
            else
            {
                name2Opt = null;
            }
        }

        private void GetUserDefinedBinaryOperatorsFromType(
            TypeSymbol constrainedToTypeOpt,
            NamedTypeSymbol type,
            BinaryOperatorKind kind,
            string name1,
            string? name2Opt,
            ArrayBuilder<BinaryOperatorSignature> operators)
        {
            Debug.Assert(operators.Count == 0);

            GetDeclaredUserDefinedBinaryOperators(constrainedToTypeOpt, type, kind, name1, operators);

            if (name2Opt is not null)
            {
                var operators2 = ArrayBuilder<BinaryOperatorSignature>.GetInstance();

                // Add regular operators as well.
                GetDeclaredUserDefinedBinaryOperators(constrainedToTypeOpt, type, kind, name2Opt, operators2);

                // Drop operators that have a match among the checked ones.
                if (operators.Count != 0)
                {
                    for (int i = operators2.Count - 1; i >= 0; i--)
                    {
                        foreach (BinaryOperatorSignature signature1 in operators)
                        {
                            if (SourceMemberContainerTypeSymbol.DoOperatorsPair(signature1.Method, operators2[i].Method))
                            {
                                operators2.RemoveAt(i);
                                break;
                            }
                        }
                    }
                }

                operators.AddRange(operators2);
                operators2.Free();
            }

            AddLiftedUserDefinedBinaryOperators(constrainedToTypeOpt, kind, operators);
        }

        private static void GetDeclaredUserDefinedBinaryOperators(TypeSymbol? constrainedToTypeOpt, NamedTypeSymbol type, BinaryOperatorKind kind, string name, ArrayBuilder<BinaryOperatorSignature> operators)
        {
            var typeOperators = ArrayBuilder<MethodSymbol>.GetInstance();
            type.AddOperators(name, typeOperators);

            foreach (MethodSymbol op in typeOperators)
            {
                // If we're in error recovery, we might have bad operators. Just ignore it.
                if (op.ParameterCount != 2 || op.ReturnsVoid)
                {
                    continue;
                }

                TypeSymbol leftOperandType = op.GetParameterType(0);
                TypeSymbol rightOperandType = op.GetParameterType(1);
                TypeSymbol resultType = op.ReturnType;

                operators.Add(new BinaryOperatorSignature(BinaryOperatorKind.UserDefined | kind, leftOperandType, rightOperandType, resultType, op, constrainedToTypeOpt));
            }

            typeOperators.Free();
        }

        void AddLiftedUserDefinedBinaryOperators(TypeSymbol? constrainedToTypeOpt, BinaryOperatorKind kind, ArrayBuilder<BinaryOperatorSignature> operators)
        {
            for (int i = operators.Count - 1; i >= 0; i--)
            {
                MethodSymbol op = operators[i].Method;
                TypeSymbol leftOperandType = op.GetParameterType(0);
                TypeSymbol rightOperandType = op.GetParameterType(1);
                TypeSymbol resultType = op.ReturnType;

                LiftingResult lifting = UserDefinedBinaryOperatorCanBeLifted(leftOperandType, rightOperandType, resultType, kind);

                if (lifting == LiftingResult.LiftOperandsAndResult)
                {
                    operators.Add(new BinaryOperatorSignature(
                        BinaryOperatorKind.Lifted | BinaryOperatorKind.UserDefined | kind,
                        MakeNullable(leftOperandType), MakeNullable(rightOperandType), MakeNullable(resultType), op, constrainedToTypeOpt));
                }
                else if (lifting == LiftingResult.LiftOperandsButNotResult)
                {
                    operators.Add(new BinaryOperatorSignature(
                        BinaryOperatorKind.Lifted | BinaryOperatorKind.UserDefined | kind,
                        MakeNullable(leftOperandType), MakeNullable(rightOperandType), resultType, op, constrainedToTypeOpt));
                }
            }
        }

#nullable disable

        private enum LiftingResult
        {
            NotLifted,
            LiftOperandsAndResult,
            LiftOperandsButNotResult
        }

        private static LiftingResult UserDefinedBinaryOperatorCanBeLifted(TypeSymbol left, TypeSymbol right, TypeSymbol result, BinaryOperatorKind kind)
        {
            // SPEC: For the binary operators + - * / % & | ^ << >> a lifted form of the
            // SPEC: operator exists if the operand and result types are all non-nullable
            // SPEC: value types. The lifted form is constructed by adding a single ?
            // SPEC: modifier to each operand and result type. 
            //
            // SPEC: For the equality operators == != a lifted form of the operator exists
            // SPEC: if the operand types are both non-nullable value types and if the 
            // SPEC: result type is bool. The lifted form is constructed by adding
            // SPEC: a single ? modifier to each operand type.
            //
            // SPEC: For the relational operators > < >= <= a lifted form of the 
            // SPEC: operator exists if the operand types are both non-nullable value
            // SPEC: types and if the result type is bool. The lifted form is 
            // SPEC: constructed by adding a single ? modifier to each operand type.

            if (!left.IsValidNullableTypeArgument() ||
                !right.IsValidNullableTypeArgument())
            {
                return LiftingResult.NotLifted;
            }

            switch (kind)
            {
                case BinaryOperatorKind.Equal:
                case BinaryOperatorKind.NotEqual:
                    // Spec violation: can't lift unless the types match.
                    // The spec doesn't require this, but dev11 does and it reduces ambiguity in some cases.
                    if (!TypeSymbol.Equals(left, right, TypeCompareKind.ConsiderEverything2)) return LiftingResult.NotLifted;
                    goto case BinaryOperatorKind.GreaterThan;
                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.LessThanOrEqual:
                    return result.SpecialType == SpecialType.System_Boolean ?
                        LiftingResult.LiftOperandsButNotResult :
                        LiftingResult.NotLifted;
                default:
                    return result.IsValidNullableTypeArgument() ?
                        LiftingResult.LiftOperandsAndResult :
                        LiftingResult.NotLifted;
            }
        }

        // Takes a list of candidates and mutates the list to throw out the ones that are worse than
        // another applicable candidate.
        private void BinaryOperatorOverloadResolution(
            BoundExpression left,
            BoundExpression right,
            BinaryOperatorOverloadResolutionResult result,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: Given the set of applicable candidate function members, the best function member in that set is located. 
            // SPEC: If the set contains only one function member, then that function member is the best function member. 

            if (result.SingleValid())
            {
                return;
            }

            var candidates = result.Results;
            RemoveLowerPriorityMembers<BinaryOperatorAnalysisResult, MethodSymbol>(candidates);

            // SPEC: Otherwise, the best function member is the one function member that is better than all other function 
            // SPEC: members with respect to the given argument list, provided that each function member is compared to all 
            // SPEC: other function members using the rules in 7.5.3.2. If there is not exactly one function member that is 
            // SPEC: better than all other function members, then the function member invocation is ambiguous and a binding-time 
            // SPEC: error occurs.

            // Try to find a single best candidate
            int bestIndex = GetTheBestCandidateIndex(left, right, candidates, ref useSiteInfo);
            if (bestIndex != -1)
            {
                // Mark all other candidates as worse
                for (int index = 0; index < candidates.Count; ++index)
                {
                    if (candidates[index].Kind != OperatorAnalysisResultKind.Inapplicable && index != bestIndex)
                    {
                        candidates[index] = candidates[index].Worse();
                    }
                }

                return;
            }

            for (int i = 1; i < candidates.Count; ++i)
            {
                if (candidates[i].Kind != OperatorAnalysisResultKind.Applicable)
                {
                    continue;
                }

                // Is this applicable operator better than every other applicable method?
                for (int j = 0; j < i; ++j)
                {
                    if (candidates[j].Kind == OperatorAnalysisResultKind.Inapplicable)
                    {
                        continue;
                    }

                    var better = BetterOperator(candidates[i].Signature, candidates[j].Signature, left, right, ref useSiteInfo);
                    if (better == BetterResult.Left)
                    {
                        candidates[j] = candidates[j].Worse();
                    }
                    else if (better == BetterResult.Right)
                    {
                        candidates[i] = candidates[i].Worse();
                    }
                }
            }
        }

        private int GetTheBestCandidateIndex(
            BoundExpression left,
            BoundExpression right,
            ArrayBuilder<BinaryOperatorAnalysisResult> candidates,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            int currentBestIndex = -1;
            for (int index = 0; index < candidates.Count; index++)
            {
                if (candidates[index].Kind != OperatorAnalysisResultKind.Applicable)
                {
                    continue;
                }

                // Assume that the current candidate is the best if we don't have any
                if (currentBestIndex == -1)
                {
                    currentBestIndex = index;
                }
                else
                {
                    var better = BetterOperator(candidates[currentBestIndex].Signature, candidates[index].Signature, left, right, ref useSiteInfo);
                    if (better == BetterResult.Right)
                    {
                        // The current best is worse
                        currentBestIndex = index;
                    }
                    else if (better != BetterResult.Left)
                    {
                        // The current best is not better
                        currentBestIndex = -1;
                    }
                }
            }

            // Make sure that every candidate up to the current best is worse
            for (int index = 0; index < currentBestIndex; index++)
            {
                if (candidates[index].Kind == OperatorAnalysisResultKind.Inapplicable)
                {
                    continue;
                }

                var better = BetterOperator(candidates[currentBestIndex].Signature, candidates[index].Signature, left, right, ref useSiteInfo);
                if (better != BetterResult.Left)
                {
                    // The current best is not better
                    return -1;
                }
            }

            return currentBestIndex;
        }

        private BetterResult BetterOperator(BinaryOperatorSignature op1, BinaryOperatorSignature op2, BoundExpression left, BoundExpression right, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // We use Priority as a tie-breaker to help match native compiler bugs.
            Debug.Assert(op1.Priority.HasValue == op2.Priority.HasValue);
            if (op1.Priority.HasValue && op1.Priority.GetValueOrDefault() != op2.Priority.GetValueOrDefault())
            {
                return (op1.Priority.GetValueOrDefault() < op2.Priority.GetValueOrDefault()) ? BetterResult.Left : BetterResult.Right;
            }

            BetterResult leftBetter = BetterConversionFromExpression(left, op1.LeftType, op2.LeftType, ref useSiteInfo);
            BetterResult rightBetter = BetterConversionFromExpression(right, op1.RightType, op2.RightType, ref useSiteInfo);

            // SPEC: Mp is defined to be a better function member than Mq if:
            // SPEC: * For each argument, the implicit conversion from Ex to Qx is not better than
            // SPEC:   the implicit conversion from Ex to Px, and
            // SPEC: * For at least one argument, the conversion from Ex to Px is better than the 
            // SPEC:   conversion from Ex to Qx.

            // If that is hard to follow, consult this handy chart:
            // op1.Left vs op2.Left     op1.Right vs op2.Right    result
            // -----------------------------------------------------------
            // op1 better               op1 better                op1 better
            // op1 better               neither better            op1 better
            // op1 better               op2 better                neither better
            // neither better           op1 better                op1 better
            // neither better           neither better            neither better
            // neither better           op2 better                op2 better
            // op2 better               op1 better                neither better
            // op2 better               neither better            op2 better
            // op2 better               op2 better                op2 better

            if (leftBetter == BetterResult.Left && rightBetter != BetterResult.Right ||
                leftBetter != BetterResult.Right && rightBetter == BetterResult.Left)
            {
                return BetterResult.Left;
            }

            if (leftBetter == BetterResult.Right && rightBetter != BetterResult.Left ||
                leftBetter != BetterResult.Left && rightBetter == BetterResult.Right)
            {
                return BetterResult.Right;
            }

            // There was no better member on the basis of conversions. Go to the tiebreaking round.

            // SPEC: In case the parameter type sequences P1, P2 and Q1, Q2 are equivalent -- that is, every Pi
            // SPEC: has an identity conversion to the corresponding Qi -- the following tie-breaking rules
            // SPEC: are applied:

            if (Conversions.HasIdentityConversion(op1.LeftType, op2.LeftType) &&
                Conversions.HasIdentityConversion(op1.RightType, op2.RightType))
            {
                // SPEC: If Mp is a non-generic method and Mq is a generic method, then Mp is better than Mq.
                if (op1.Method?.GetMemberArityIncludingExtension() is null or 0)
                {
                    if (op2.Method?.GetMemberArityIncludingExtension() > 0)
                    {
                        return BetterResult.Left;
                    }
                }
                else if (op2.Method?.GetMemberArityIncludingExtension() is null or 0)
                {
                    return BetterResult.Right;
                }

                // NOTE: The native compiler does not follow these rules; effectively, the native 
                // compiler checks for liftedness first, and then for specificity. For example:
                // struct S<T> where T : struct {
                //   public static bool operator +(S<T> x, int y) { return true; }
                //   public static bool? operator +(S<T>? x, int? y) { return false; }
                // }
                // 
                // bool? b = new S<int>?() + new int?();
                //
                // should reason as follows: the two applicable operators are the lifted
                // form of the first operator and the unlifted second operator. The
                // lifted form of the first operator is *more specific* because int?
                // is more specific than T?.  Therefore it should win. In fact the 
                // native compiler chooses the second operator, because it is unlifted.
                // 
                // Roslyn follows the spec rules; if we decide to change the spec to match
                // the native compiler, or decide to change Roslyn to match the native
                // compiler, we should change the order of the checks here.

                // SPEC: If Mp has more specific parameter types than Mq then Mp is better than Mq.
                BetterResult result = MoreSpecificOperator(op1, op2, ref useSiteInfo);
                if (result == BetterResult.Left || result == BetterResult.Right)
                {
                    return result;
                }

                // SPEC: If one member is a non-lifted operator and the other is a lifted operator,
                // SPEC: the non-lifted one is better.

                bool lifted1 = op1.Kind.IsLifted();
                bool lifted2 = op2.Kind.IsLifted();

                if (lifted1 && !lifted2)
                {
                    return BetterResult.Right;
                }
                else if (!lifted1 && lifted2)
                {
                    return BetterResult.Left;
                }
            }

            // Always prefer operators with val parameters over operators with in parameters:
            BetterResult valOverInPreference;

            if (op1.LeftRefKind == RefKind.None && op2.LeftRefKind == RefKind.In)
            {
                valOverInPreference = BetterResult.Left;
            }
            else if (op2.LeftRefKind == RefKind.None && op1.LeftRefKind == RefKind.In)
            {
                valOverInPreference = BetterResult.Right;
            }
            else
            {
                valOverInPreference = BetterResult.Neither;
            }

            if (op1.RightRefKind == RefKind.None && op2.RightRefKind == RefKind.In)
            {
                if (valOverInPreference == BetterResult.Right)
                {
                    return BetterResult.Neither;
                }
                else
                {
                    valOverInPreference = BetterResult.Left;
                }
            }
            else if (op2.RightRefKind == RefKind.None && op1.RightRefKind == RefKind.In)
            {
                if (valOverInPreference == BetterResult.Left)
                {
                    return BetterResult.Neither;
                }
                else
                {
                    valOverInPreference = BetterResult.Right;
                }
            }

            return valOverInPreference;
        }

        private BetterResult MoreSpecificOperator(BinaryOperatorSignature op1, BinaryOperatorSignature op2, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            TypeSymbol op1Left, op1Right, op2Left, op2Right;
            if ((object)op1.Method != null)
            {
                var p = op1.Method.OriginalDefinition.GetParameters();
                op1Left = p[0].Type;
                op1Right = p[1].Type;
                if (op1.Kind.IsLifted())
                {
                    op1Left = MakeNullable(op1Left);
                    op1Right = MakeNullable(op1Right);
                }
            }
            else
            {
                op1Left = op1.LeftType;
                op1Right = op1.RightType;
            }

            if ((object)op2.Method != null)
            {
                var p = op2.Method.OriginalDefinition.GetParameters();
                op2Left = p[0].Type;
                op2Right = p[1].Type;
                if (op2.Kind.IsLifted())
                {
                    op2Left = MakeNullable(op2Left);
                    op2Right = MakeNullable(op2Right);
                }
            }
            else
            {
                op2Left = op2.LeftType;
                op2Right = op2.RightType;
            }

            using var uninst1 = TemporaryArray<TypeSymbol>.Empty;
            using var uninst2 = TemporaryArray<TypeSymbol>.Empty;

            uninst1.Add(op1Left);
            uninst1.Add(op1Right);

            uninst2.Add(op2Left);
            uninst2.Add(op2Right);

            BetterResult result = MoreSpecificType(ref uninst1.AsRef(), ref uninst2.AsRef(), ref useSiteInfo);

            return result;
        }

        [Conditional("DEBUG")]
        private static void AssertNotChecked(BinaryOperatorKind kind)
        {
            Debug.Assert((kind & ~BinaryOperatorKind.Checked) == kind, "Did not expect operator to be checked.  Consider using .Operator() to mask.");
        }

#nullable enable 

        public bool BinaryOperatorExtensionOverloadResolutionInSingleScope(
            ArrayBuilder<NamedTypeSymbol> extensionDeclarationsInSingleScope,
            BinaryOperatorKind kind,
            bool isChecked,
            string name1,
            string? name2Opt,
            BoundExpression left,
            BoundExpression right,
            BinaryOperatorOverloadResolutionResult result,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(isChecked || name2Opt is null);

            var operators = ArrayBuilder<BinaryOperatorSignature>.GetInstance();

            getDeclaredUserDefinedBinaryOperatorsInScope(extensionDeclarationsInSingleScope, kind, name1, name2Opt, operators);

            if (left.Type?.IsNullableType() == true || right.Type?.IsNullableType() == true) // Wouldn't be applicable to the receiver type otherwise
            {
                AddLiftedUserDefinedBinaryOperators(constrainedToTypeOpt: null, kind, operators);
            }

            inferTypeArgumentsAndRemoveInapplicableToReceiverType(kind, left, right, operators, ref useSiteInfo);

            bool hadApplicableCandidates = false;

            if (!operators.IsEmpty)
            {
                var results = result.Results;
                results.Clear();
                if (CandidateOperators(isChecked, operators, left, right, results, ref useSiteInfo))
                {
                    BinaryOperatorOverloadResolution(left, right, result, ref useSiteInfo);
                    hadApplicableCandidates = true;
                }
            }

            operators.Free();

            return hadApplicableCandidates;

            static void getDeclaredUserDefinedBinaryOperatorsInScope(ArrayBuilder<NamedTypeSymbol> extensionDeclarationsInSingleScope, BinaryOperatorKind kind, string name1, string? name2Opt, ArrayBuilder<BinaryOperatorSignature> operators)
            {
                getDeclaredUserDefinedBinaryOperators(extensionDeclarationsInSingleScope, kind, name1, operators);

                if (name2Opt is not null)
                {
                    if (!operators.IsEmpty)
                    {
                        var existing = new HashSet<MethodSymbol>(PairedExtensionOperatorSignatureComparer.Instance);
                        existing.AddRange(operators.Select(static (op) => op.Method));

                        var operators2 = ArrayBuilder<BinaryOperatorSignature>.GetInstance();
                        getDeclaredUserDefinedBinaryOperators(extensionDeclarationsInSingleScope, kind, name2Opt, operators2);

                        foreach (var op in operators2)
                        {
                            if (!existing.Contains(op.Method))
                            {
                                operators.Add(op);
                            }
                        }

                        operators2.Free();
                    }
                    else
                    {
                        getDeclaredUserDefinedBinaryOperators(extensionDeclarationsInSingleScope, kind, name2Opt, operators);
                    }
                }
            }

            static void getDeclaredUserDefinedBinaryOperators(ArrayBuilder<NamedTypeSymbol> extensionDeclarationsInSingleScope, BinaryOperatorKind kind, string name, ArrayBuilder<BinaryOperatorSignature> operators)
            {
                foreach (NamedTypeSymbol extensionDeclaration in extensionDeclarationsInSingleScope)
                {
                    Debug.Assert(extensionDeclaration.IsExtension);

                    if (extensionDeclaration.ExtensionParameter is null)
                    {
                        continue;
                    }

                    GetDeclaredUserDefinedBinaryOperators(constrainedToTypeOpt: null, extensionDeclaration, kind, name, operators);
                }
            }

            void inferTypeArgumentsAndRemoveInapplicableToReceiverType(BinaryOperatorKind kind, BoundExpression left, BoundExpression right, ArrayBuilder<BinaryOperatorSignature> operators, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
            {
                for (int i = operators.Count - 1; i >= 0; i--)
                {
                    var candidate = operators[i];
                    MethodSymbol method = candidate.Method;
                    NamedTypeSymbol extension = method.ContainingType;

                    if (extension.Arity == 0)
                    {
                        if (isApplicableToReceiver(in candidate, left, right, ref useSiteInfo))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // Infer type arguments 
                        var inferenceResult = MethodTypeInferrer.Infer(
                            _binder,
                            this.Conversions,
                            extension.TypeParameters,
                            extension,
                            [TypeWithAnnotations.Create(candidate.LeftType), TypeWithAnnotations.Create(candidate.RightType)],
                            method.ParameterRefKinds,
                            [left, right],
                            ref useSiteInfo,
                            ordinals: null);

                        if (inferenceResult.Success)
                        {
                            extension = extension.Construct(inferenceResult.InferredTypeArguments);
                            method = method.AsMember(extension);

                            if (!FailsConstraintChecks(method, out ArrayBuilder<TypeParameterDiagnosticInfo> constraintFailureDiagnosticsOpt, template: CompoundUseSiteInfo<AssemblySymbol>.Discarded))
                            {
                                TypeSymbol leftOperandType = method.GetParameterType(0);
                                TypeSymbol rightOperandType = method.GetParameterType(1);
                                TypeSymbol resultType = method.ReturnType;

                                BinaryOperatorSignature inferredCandidate;

                                if (candidate.Kind.IsLifted())
                                {
                                    LiftingResult lifting = UserDefinedBinaryOperatorCanBeLifted(leftOperandType, rightOperandType, resultType, kind);
                                    Debug.Assert(lifting is LiftingResult.LiftOperandsAndResult or LiftingResult.LiftOperandsButNotResult);

                                    inferredCandidate = new BinaryOperatorSignature(
                                        BinaryOperatorKind.Lifted | BinaryOperatorKind.UserDefined | kind,
                                        MakeNullable(leftOperandType),
                                        MakeNullable(rightOperandType),
                                        lifting == LiftingResult.LiftOperandsButNotResult ? resultType : MakeNullable(resultType),
                                        method, constrainedToTypeOpt: null);
                                }
                                else
                                {
                                    inferredCandidate = new BinaryOperatorSignature(BinaryOperatorKind.UserDefined | kind, leftOperandType, rightOperandType, resultType, method, constrainedToTypeOpt: null);
                                }

                                if (isApplicableToReceiver(in inferredCandidate, left, right, ref useSiteInfo))
                                {
                                    operators[i] = inferredCandidate;
                                    continue;
                                }
                            }

                            constraintFailureDiagnosticsOpt?.Free();
                        }
                    }

                    operators.RemoveAt(i);
                }
            }

            bool isApplicableToReceiver(in BinaryOperatorSignature candidate, BoundExpression left, BoundExpression right, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
            {
                Debug.Assert(candidate.Method.ContainingType.ExtensionParameter is not null);

                if (left.Type is not null && parameterMatchesReceiver(in candidate, 0) && isOperandApplicableToReceiver(in candidate, left, ref useSiteInfo))
                {
                    return true;
                }

                if (!kind.IsShift() && right.Type is not null && parameterMatchesReceiver(in candidate, 1) && isOperandApplicableToReceiver(in candidate, right, ref useSiteInfo))
                {
                    return true;
                }

                return false;
            }

            static bool parameterMatchesReceiver(in BinaryOperatorSignature candidate, int paramIndex)
            {
                var method = candidate.Method.OriginalDefinition;
                var extensionParameter = method.ContainingType.ExtensionParameter;
                Debug.Assert(extensionParameter is not null);

                return SourceUserDefinedOperatorSymbolBase.ExtensionOperatorParameterTypeMatchesExtendedType(method.Parameters[paramIndex].Type, extensionParameter.Type);
            }

            bool isOperandApplicableToReceiver(in BinaryOperatorSignature candidate, BoundExpression operand, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
            {
                Debug.Assert(operand.Type is not null);
                Debug.Assert(candidate.Method.ContainingType.ExtensionParameter is not null);

                if (candidate.Kind.IsLifted() && operand.Type.IsNullableType())
                {
                    if (!candidate.Method.ContainingType.ExtensionParameter.Type.IsValidNullableTypeArgument() ||
                        !Conversions.ConvertExtensionMethodThisArg(MakeNullable(candidate.Method.ContainingType.ExtensionParameter.Type), operand.Type, ref useSiteInfo, isMethodGroupConversion: false).Exists)
                    {
                        return false;
                    }
                }
                else if (!Conversions.ConvertExtensionMethodThisArg(candidate.Method.ContainingType.ExtensionParameter.Type, operand.Type, ref useSiteInfo, isMethodGroupConversion: false).Exists)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
