namespace Bond.Parser.Syntax;

public enum TypeConstraint
{
    None,
    Value
}

public record TypeParam(
    string Name,
    TypeConstraint Constraint = TypeConstraint.None
)
{
    public override string ToString() =>
        Constraint == TypeConstraint.Value ? $"{Name} : value" : Name;
}
