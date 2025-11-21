using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

using Xunit.Internal;

namespace Hugo.Assertions;

// Compatibility layer that preserves the existing Shouldly-style helpers while routing
// assertions through AwesomeAssertions. This keeps the tests concise without depending
// on the old Shouldly package.
public static class AwesomeAssertionExtensions
{
    private static string Because(string? because) => because ?? string.Empty;

    public static IEnumerable<T> ShouldBe<T>(this IEnumerable<T> actual, IEnumerable<T> expected, string? because = null, params object[] becauseArgs)
    {
        actual.Should().Equal(expected, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldBe<T>(this T actual, T expected, string? because = null, params object[] becauseArgs)
    {
        if (actual is not string && actual is IEnumerable actualEnumerable && expected is IEnumerable expectedEnumerable)
        {
            // Prefer structural comparison for sequences (arrays, lists, etc.) to avoid reference equality pitfalls.
            actualEnumerable.Should().BeEquivalentTo(expectedEnumerable, Because(because), becauseArgs);
        }
        else
        {
            actual.Should().Be(expected, Because(because), becauseArgs);
        }
        return actual;
    }

    public static double ShouldBe(this double actual, double expected, double tolerance, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeApproximately(expected, tolerance, Because(because), becauseArgs);
        return actual;
    }

    public static float ShouldBe(this float actual, float expected, float tolerance, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeApproximately(expected, tolerance, Because(because), becauseArgs);
        return actual;
    }

    public static DateTimeOffset ShouldBe(this DateTimeOffset actual, DateTimeOffset expected, TimeSpan precision, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeCloseTo(expected, precision, Because(because), becauseArgs);
        return actual;
    }

    public static DateTime ShouldBe(this DateTime actual, DateTime expected, TimeSpan precision, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeCloseTo(expected, precision, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldNotBe<T>(this T actual, T expected, string? because = null, params object[] becauseArgs)
    {
        actual.Should().NotBe(expected, Because(because), becauseArgs);
        return actual;
    }

    public static bool ShouldBeTrue(this bool actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeTrue(Because(because), becauseArgs);
        return actual;
    }

    public static bool ShouldBeFalse(this bool actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeFalse(Because(because), becauseArgs);
        return actual;
    }

    public static T? ShouldBeNull<T>(this T? actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeNull(Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldNotBeNull<T>([NotNull] this T? actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().NotBeNull(Because(because), becauseArgs);
        return actual!;
    }

    public static IEnumerable<T> ShouldBeEmpty<T>(this IEnumerable<T> actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeEmpty(Because(because), becauseArgs);
        return actual;
    }

    public static string ShouldBeEmpty(this string actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().BeEmpty(Because(because), becauseArgs);
        return actual;
    }

    public static IEnumerable<T> ShouldNotBeEmpty<T>(this IEnumerable<T> actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().NotBeEmpty(Because(because), becauseArgs);
        return actual;
    }

    public static string ShouldNotBeEmpty(this string actual, string? because = null, params object[] becauseArgs)
    {
        actual.Should().NotBeNullOrEmpty(Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldBeGreaterThan<T>(this T actual, T expected, string? because = null, params object[] becauseArgs)
        where T : IComparable<T>
    {
        actual.Should().BeGreaterThan(expected, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldBeGreaterThanOrEqualTo<T>(this T actual, T expected, string? because = null, params object[] becauseArgs)
        where T : IComparable<T>
    {
        actual.Should().BeGreaterThanOrEqualTo(expected, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldBeLessThanOrEqualTo<T>(this T actual, T expected, string? because = null, params object[] becauseArgs)
        where T : IComparable<T>
    {
        actual.Should().BeLessThanOrEqualTo(expected, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldBeInRange<T>(this T actual, T minimum, T maximum, string? because = null, params object[] becauseArgs)
        where T : struct, IComparable<T>
    {
        actual.Should().BeInRange(minimum, maximum, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldBeOneOf<T>(this T actual, params T[] expected)
    {
        if (!expected.Contains(actual))
        {
            actual.Should().BeOneOf(expected);
        }
        return actual;
    }

    public static T ShouldBeSameAs<T>(this T actual, object? expected, string? because = null, params object[] becauseArgs)
        where T : class?
    {
        actual.Should().BeSameAs(expected, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldNotBeSameAs<T>(this T actual, object? expected, string? because = null, params object[] becauseArgs)
        where T : class?
    {
        actual.Should().NotBeSameAs(expected, Because(because), becauseArgs);
        return actual;
    }

    public static IEnumerable<T> ShouldAllBe<T>(this IEnumerable<T> actual, Expression<Func<T, bool>> predicate, string? because = null, params object[] becauseArgs)
    {
        actual.Should().OnlyContain(predicate, Because(because), becauseArgs);
        return actual;
    }

    public static IEnumerable<T> ShouldContain<T>(this IEnumerable<T> actual, T expected, string? because = null, params object[] becauseArgs)
    {
        actual.Should().Contain(expected, Because(because), becauseArgs);
        return actual;
    }

    public static IEnumerable<T> ShouldContain<T>(this IEnumerable<T> actual, Expression<Func<T, bool>> predicate, string? because = null, params object[] becauseArgs)
    {
        actual.Should().Contain(predicate, Because(because), becauseArgs);
        return actual;
    }

    public static string ShouldContain(this string actual, string expected, string? because = null, params object[] becauseArgs)
    {
        actual.Should().Contain(expected, Because(because), becauseArgs);
        return actual;
    }

    public static string ShouldStartWith(this string actual, string expected, string? because = null, params object[] becauseArgs)
    {
        actual.Should().StartWith(expected, Because(because), becauseArgs);
        return actual;
    }

    public static T ShouldHaveSingleItem<T>(this IEnumerable<T> actual, string? because = null, params object[] becauseArgs)
    {
        return actual.Should().ContainSingle(Because(because), becauseArgs).Which;
    }

    public static TTarget ShouldBeOfType<TTarget>(this object? actual, string? because = null, params object[] becauseArgs)
    {
        return actual.Should().BeOfType<TTarget>(Because(because), becauseArgs).Which;
    }

    public static TTarget ShouldBeAssignableTo<TTarget>(this object? actual, string? because = null, params object[] becauseArgs)
    {
        return actual.Should().BeAssignableTo<TTarget>(Because(because), becauseArgs).Which;
    }

    public static IReadOnlyDictionary<TKey, TValue> ShouldContainKey<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> actual, TKey key, string? because = null, params object[] becauseArgs)
    {
        actual.Should().ContainKey(key, Because(because), becauseArgs);
        return actual;
    }

    public static IReadOnlyDictionary<TKey, TValue> ShouldContainKeyAndValue<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> actual, TKey key, TValue value, string? because = null, params object[] becauseArgs)
    {
        actual.Should().ContainKey(key, Because(because), becauseArgs);
        actual[key].Should().Be(value, Because(because), becauseArgs);
        return actual;
    }
}

public static class Should
{
    private static string Because(string? because) => because ?? string.Empty;

    public static TException Throw<TException>(Action action, string? because = null, params object[] becauseArgs)
        where TException : Exception
    {
        return action.Should().Throw<TException>(Because(because), becauseArgs).Which;
    }

    public static TException Throw<TException>(Func<object?> func, string? because = null, params object[] becauseArgs)
        where TException : Exception
    {
        Action action = () => _ = func();
        return action.Should().Throw<TException>(Because(because), becauseArgs).Which;
    }

    public static async ValueTask<TException> ThrowAsync<TException>(Func<ValueTask> action, string? because = null, params object[] becauseArgs)
        where TException : Exception
    {
        var act = async Task () =>
            {
                await action.Invoke();
            };
        var assertion = await act.Should().ThrowAsync<TException>(Because(because), becauseArgs).ConfigureAwait(false);
        return assertion.Which;
    }

    public static void NotThrow(Action action, string? because = null, params object[] becauseArgs)
    {
        action.Should().NotThrow(Because(because), becauseArgs);
    }

    public static async ValueTask NotThrowAsync(Func<ValueTask> action, string? because = null, params object[] becauseArgs)
    {
        var act = async Task () =>
            {
                await action.Invoke();
            };
        await act.Should().NotThrowAsync(Because(because), becauseArgs);
    }
}
