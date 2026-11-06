namespace Chetah.Postgres;

/// <summary>Expression-only SQL aggregate markers; these methods cannot execute outside a query.</summary>
public static class Sql
{
    private static InvalidOperationException OutsideQuery() => new("SQL aggregate functions can only be used inside a Chetah query expression.");
    public static long Count() => throw OutsideQuery();
    public static long Count<TValue>(TValue value) => throw OutsideQuery();
    public static long? Sum(int value) => throw OutsideQuery();
    public static long? Sum(int? value) => throw OutsideQuery();
    public static decimal? Sum(long value) => throw OutsideQuery();
    public static decimal? Sum(long? value) => throw OutsideQuery();
    public static decimal? Average(int value) => throw OutsideQuery();
    public static decimal? Average(int? value) => throw OutsideQuery();
    public static decimal? Average(long value) => throw OutsideQuery();
    public static decimal? Average(long? value) => throw OutsideQuery();
    public static TValue Min<TValue>(TValue value) => throw OutsideQuery();
    public static TValue Max<TValue>(TValue value) => throw OutsideQuery();
}
