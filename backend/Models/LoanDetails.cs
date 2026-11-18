namespace MoneyTracker.Models;

/// <summary>
/// Terms of a Loan account, for its loan panel (payoff projections) and the
/// automatic interest / escrow charges (LoanService). One per loan account.
/// </summary>
public class LoanDetails
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    /// <summary>Annual interest rate in percent, e.g. 6.5.</summary>
    public decimal AnnualRate { get; set; }
    /// <summary>Original amount borrowed (optional; used to work out the payment).</summary>
    public decimal? OriginalAmount { get; set; }
    /// <summary>Length of the loan in months (optional; used to work out the payment).</summary>
    public int? TermMonths { get; set; }
    /// <summary>Date of the first payment (sets the payment day each month).</summary>
    public DateOnly? FirstPaymentDate { get; set; }
    /// <summary>Monthly principal + interest; when null it's worked out from amount, rate and term.</summary>
    public decimal? MonthlyPayment { get; set; }
    /// <summary>Monthly escrow (property tax, insurance) included in each payment; 0 for none.</summary>
    public decimal EscrowAmount { get; set; }

    /// <summary>Add interest (and escrow) charges to the loan automatically for each payment.</summary>
    public bool AutoCharges { get; set; } = true;
    /// <summary>Only payments on or after this date get automatic charges (earlier history is left alone).</summary>
    public DateOnly AutoChargesFrom { get; set; }
    public int? InterestCategoryId { get; set; }
    public Category? InterestCategory { get; set; }
    public int? EscrowCategoryId { get; set; }
    public Category? EscrowCategory { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
