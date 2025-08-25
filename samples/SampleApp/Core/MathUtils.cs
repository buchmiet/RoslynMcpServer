namespace SampleApp.Core;

public static class MathUtils
{
    public static int Counter;

    public static double Pi { get; set; } = 3.1415926535;

    public static int Add(int a, int b)
    {
        Counter++;
        return Helper.Increment(a) + b;
    }

    public static int Multiply(int a, int b)
    {
        return a * b;
    }
}

public static class Helper
{
    public static int Increment(int x) => x + 1;
}

public interface ICalculator
{
    int Compute(int a, int b);
    string Name { get; }
}

public interface IAdvancedCalculator : ICalculator
{
    int Extra(int x);
}

public abstract class CalculatorBase : ICalculator
{
    public abstract int Compute(int a, int b);
    public virtual string Name => "Base";
}

public class BasicCalculator : CalculatorBase
{
    public override int Compute(int a, int b) => a + b;
    public override string Name => "Basic";
}

public class AdvancedCalculator : BasicCalculator, IAdvancedCalculator
{
    public int Extra(int x) => x * x;
    public override string Name => "Advanced";
}
