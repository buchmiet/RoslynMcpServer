using SampleApp.Core;

namespace SampleApp.Services;

public class OrderService
{
    public int Process(int quantity, int unitPrice)
    {
        // Uses Core.MathUtils to trigger cross-namespace references
        var subtotal = MathUtils.Multiply(quantity, unitPrice);
        return MathUtils.Add(subtotal, 0);
    }
}

