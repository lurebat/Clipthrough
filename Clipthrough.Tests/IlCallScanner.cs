using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
            byte[]? il;
            try
            {
                il = body.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception)
            {
                continue;
            }

            if (il is null)
            {
                continue;
            }

            var methodArguments = body.IsGenericMethodDefinition ? body.GetGenericArguments() : null;

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
        }

        return count;
    }
}
