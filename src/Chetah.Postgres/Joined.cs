namespace Chetah.Postgres;

public sealed record Joined<TLeft, TRight>(TLeft Left, TRight Right);
