using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Clipthrough.Tests;

/// <summary>
/// Finds calls to a given method in a type's IL.
///
/// Used for contracts that are about wiring rather than behaviour - "every view
/// model observes its command errors", "every progress stream is rate-limited" -
/// where the alternative is either constructing the whole object graph or
/// trusting a comment.
/// </summary>
internal static class IlCallScanner
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static bool CallsMethod(Type owner, MethodInfo target) => CountCalls(owner, target) > 0;

    /// <summary>
    /// Counts call sites in one method body only. Use this for contracts about a
    /// single member - "this property getter does no I/O" - where a whole-type scan
    /// cannot tell the getter apart from a legitimate call elsewhere in the class.
    /// </summary>
    public static int CountCallsIn(MethodBase body, MethodInfo target)
    {
        // An async or iterator method body is a stub that starts a compiler-generated
        // state machine; everything the source appears to call lives in that type's
        // MoveNext instead. Scanning the declared method finds nothing, which reads as
        // a passing "makes no such call" assertion when the call is plainly there.
        var stateMachine = body.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? body.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;

        return stateMachine is not null
            ? CountCalls(stateMachine, target)
            : CountCallsInBody(body, body.DeclaringType?.Module ?? target.Module, null, target);
    }

    /// <summary>
    /// Counts call sites, including inside compiler-generated nested types - lambdas
    /// and async state machines are where most of this wiring actually lives.
    /// </summary>
    public static int CountCalls(Type owner, MethodInfo target)
    {
        var total = 0;
        foreach (var type in WithNestedTypes(owner))
        {
            total += CountCallsInType(type, target);
        }

        return total;
    }

    private static IEnumerable<Type> WithNestedTypes(Type owner)
    {
        yield return owner;
        foreach (var nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var deeper in WithNestedTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    private static int CountCallsInType(Type owner, MethodInfo target)
    {
        var module = owner.Module;
        var typeArguments = owner.IsGenericType ? owner.GetGenericArguments() : null;

        var bodies = owner.GetConstructors(All).Cast<MethodBase>()
            .Concat(owner.GetMethods(All));

        var count = 0;
        foreach (var body in bodies)
        {
            count += CountCallsInBody(body, module, typeArguments, target);
        }

        return count;
    }

    private static int CountCallsInBody(MethodBase body, Module module, Type[]? typeArguments, MethodInfo target)
    {
        byte[]? il;
        try
        {
            il = body.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            return 0;
        }

        if (il is null)
        {
            return 0;
        }

        var methodArguments = body.IsGenericMethodDefinition ? body.GetGenericArguments() : null;
        var count = 0;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);

            // The scan is not opcode-length aware, so most candidates here are
            // operand bytes that only look like a call. Those fail to resolve.
            MethodBase? called;
            try
            {
                called = module.ResolveMethod(token, typeArguments, methodArguments);
            }
            catch (Exception)
            {
                continue;
            }

            if (called is null)
            {
                continue;
            }

            if (called is MethodInfo info && info.IsGenericMethod && !info.IsGenericMethodDefinition)
            {
                called = info.GetGenericMethodDefinition();
            }

            if (called.MetadataToken == target.MetadataToken && called.Module == target.Module)
            {
                count++;
            }
        }

        return count;
    }
}
