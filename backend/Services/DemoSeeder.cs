using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Services;

public class DemoSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IEncryptionService encryption)
{
    public const string DemoEmail    = "demo@example.com";
    public const string DemoPassword = "Demo123456!!";

    public async Task SeedIfNeededAsync()
    {
        if (await userManager.FindByEmailAsync(DemoEmail) is not null) return;
        await SeedAsync();
    }

    public async Task ResetAsync()
    {
        var user = await userManager.FindByEmailAsync(DemoEmail);
        if (user is not null)
        {
            // Delete owned data in dependency order
            var accountIds = await db.Accounts.Where(a => a.UserId == user.Id).Select(a => a.Id).ToListAsync();
            await db.Transactions.Where(t => accountIds.Contains(t.AccountId)).ExecuteDeleteAsync();
            await db.ScheduledTransactions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
            await db.Payees.Where(p => p.UserId == user.Id).ExecuteDeleteAsync();
            await db.Categories.Where(c => c.UserId == user.Id).ExecuteDeleteAsync();
            await db.Accounts.Where(a => a.UserId == user.Id).ExecuteDeleteAsync();
            await db.Institutions.Where(i => i.UserId == user.Id).ExecuteDeleteAsync();
            await db.RefreshTokens.Where(r => r.UserId == user.Id).ExecuteDeleteAsync();
            await db.AuditLogs.Where(a => a.UserId == user.Id).ExecuteDeleteAsync();
            await userManager.DeleteAsync(user);
        }
        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        // ── Roles ──────────────────────────────────────────────────────────────
        foreach (var role in new[] { "Admin", "Standard" })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        // ── User ───────────────────────────────────────────────────────────────
        var user = new ApplicationUser
        {
            UserName         = DemoEmail,
            Email            = DemoEmail,
            EncryptedDataKey = encryption.GenerateEncryptedDek(),
            MfaEnrolled      = true,   // skip MFA setup redirect; login bypasses challenge
            // Seeded so "Try Demo" lands straight in the app instead of on the
            // mandatory timezone picker. The demo data is authored around US
            // dates, so Eastern is the honest match.
            TimeZoneId       = "America/New_York",
        };
        var result = await userManager.CreateAsync(user, DemoPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException("Demo user creation failed: " + string.Join(", ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, "Standard");

        var dek   = user.EncryptedDataKey;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rng   = new Random(42);

        // ── Institutions ───────────────────────────────────────────────────────
        var chase  = new Institution { UserId = user.Id, Name = "Chase Bank" };
        var wf     = new Institution { UserId = user.Id, Name = "Wells Fargo Bank" };
        var honda  = new Institution { UserId = user.Id, Name = "Honda Financial Services" };
        db.Institutions.AddRange(chase, wf, honda);
        await db.SaveChangesAsync();

        // ── Accounts ───────────────────────────────────────────────────────────
        var checking = new Account
        {
            UserId = user.Id, Name = "Chase Checking", Type = AccountType.Checking,
            OpeningBalance = 4_250m, InstitutionId = chase.Id, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        var savings = new Account
        {
            UserId = user.Id, Name = "Chase Savings", Type = AccountType.Savings,
            OpeningBalance = 11_800m, InstitutionId = chase.Id, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        var cc = new Account
        {
            UserId = user.Id, Name = "Chase Sapphire Reserve", Type = AccountType.CreditCard,
            OpeningBalance = 0m, InstitutionId = chase.Id, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        var mortgage = new Account
        {
            UserId = user.Id, Name = "Home Mortgage", Type = AccountType.Loan,
            OpeningBalance = -287_400m, InstitutionId = wf.Id, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        var carLoan = new Account
        {
            UserId = user.Id, Name = "Honda Accord Loan", Type = AccountType.Loan,
            OpeningBalance = -18_650m, InstitutionId = honda.Id, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        var oldCard = new Account
        {
            UserId = user.Id, Name = "Chase Freedom (old)", Type = AccountType.CreditCard,
            OpeningBalance = 0m, InstitutionId = chase.Id, IsActive = false, CreatedAt = DateTime.UtcNow,
        };
        db.Accounts.AddRange(checking, savings, cc, mortgage, carLoan, oldCard);
        await db.SaveChangesAsync();

        // ── Categories ─────────────────────────────────────────────────────────
        Category Cat(string name, int? parentId = null) => new()
        {
            UserId = user.Id, NameEncrypted = encryption.Encrypt(name, dek)!, ParentId = parentId,
        };

        var catIncome  = Cat("Income");
        var catHousing = Cat("Housing");
        var catFood    = Cat("Food & Dining");
        var catTranspo = Cat("Transportation");
        var catUtils   = Cat("Utilities");
        var catEntmt   = Cat("Entertainment");
        var catHealth  = Cat("Healthcare");
        var catShop    = Cat("Shopping");
        var catPersonal = Cat("Personal");
        db.Categories.AddRange(catIncome, catHousing, catFood, catTranspo, catUtils, catEntmt, catHealth, catShop, catPersonal);
        await db.SaveChangesAsync();

        var subSalary   = Cat("Salary",              catIncome.Id);
        var subInterest = Cat("Interest",             catIncome.Id);
        var subMortgage = Cat("Mortgage",             catHousing.Id);
        var subHomeIns  = Cat("Home Insurance",       catHousing.Id);
        var subRepairs  = Cat("Home Repairs",         catHousing.Id);
        var subGrocery  = Cat("Groceries",            catFood.Id);
        var subRestaurant = Cat("Restaurants",        catFood.Id);
        var subCoffee   = Cat("Coffee",               catFood.Id);
        var subGas      = Cat("Gas",                  catTranspo.Id);
        var subCarIns   = Cat("Car Insurance",        catTranspo.Id);
        var subElectric = Cat("Electric",             catUtils.Id);
        var subInternet = Cat("Internet",             catUtils.Id);
        var subPhone    = Cat("Phone",                catUtils.Id);
        var subStream   = Cat("Streaming",            catEntmt.Id);
        var subMovies   = Cat("Movies & Events",      catEntmt.Id);
        var subDoctor   = Cat("Doctor & Copays",      catHealth.Id);
        var subPharmacy = Cat("Pharmacy",             catHealth.Id);
        var subAmazon   = Cat("Amazon",               catShop.Id);
        var subClothing = Cat("Clothing",             catShop.Id);
        var subGym      = Cat("Gym",                  catPersonal.Id);
        db.Categories.AddRange(subSalary, subInterest, subMortgage, subHomeIns, subRepairs,
            subGrocery, subRestaurant, subCoffee, subGas, subCarIns,
            subElectric, subInternet, subPhone, subStream, subMovies,
            subDoctor, subPharmacy, subAmazon, subClothing, subGym);
        await db.SaveChangesAsync();

        // ── Payees ─────────────────────────────────────────────────────────────
        Payee Payee(string name, int? defaultCategoryId = null) => new()
        {
            UserId = user.Id, NameEncrypted = encryption.Encrypt(name, dek)!, DefaultCategoryId = defaultCategoryId,
        };

        var payEmployer     = Payee("Acme Corporation",          subSalary.Id);
        var payWFMortgage   = Payee("Wells Fargo Mortgage",      subMortgage.Id);
        var payHonda        = Payee("Honda Financial Services",  null);
        var payStateFarm    = Payee("State Farm",                subHomeIns.Id);
        var payWholefoods   = Payee("Whole Foods Market",        subGrocery.Id);
        var payTraderJoes   = Payee("Trader Joe's",              subGrocery.Id);
        var payCostco       = Payee("Costco",                    subGrocery.Id);
        var payShell        = Payee("Shell",                     subGas.Id);
        var payChevron      = Payee("Chevron",                   subGas.Id);
        var payNetflix      = Payee("Netflix",                   subStream.Id);
        var paySpotify      = Payee("Spotify",                   subStream.Id);
        var payAmazon       = Payee("Amazon",                    subAmazon.Id);
        var payTarget       = Payee("Target",                    null);
        var payStarbucks    = Payee("Starbucks",                 subCoffee.Id);
        var payChipotle     = Payee("Chipotle",                  subRestaurant.Id);
        var payOlive        = Payee("Olive Garden",              subRestaurant.Id);
        var payTexasRoadhse = Payee("Texas Roadhouse",           subRestaurant.Id);
        var payPGE          = Payee("PG&E",                      subElectric.Id);
        var payComcast      = Payee("Comcast",                   subInternet.Id);
        var payATT          = Payee("AT&T",                      subPhone.Id);
        var payGeicoCarIns  = Payee("GEICO",                     subCarIns.Id);
        var payCVS          = Payee("CVS Pharmacy",              subPharmacy.Id);
        var payKaiser       = Payee("Kaiser Permanente",         subDoctor.Id);
        var payPlanetFit    = Payee("Planet Fitness",            subGym.Id);
        var payHnM          = Payee("H&M",                       subClothing.Id);
        var payHomeDepot    = Payee("Home Depot",                subRepairs.Id);

        db.Payees.AddRange(payEmployer, payWFMortgage, payHonda, payStateFarm,
            payWholefoods, payTraderJoes, payCostco, payShell, payChevron,
            payNetflix, paySpotify, payAmazon, payTarget, payStarbucks,
            payChipotle, payOlive, payTexasRoadhse, payPGE, payComcast,
            payATT, payGeicoCarIns, payCVS, payKaiser, payPlanetFit, payHnM, payHomeDepot);
        await db.SaveChangesAsync();

        // ── Transactions (6 months of history) ────────────────────────────────

        var txList = new List<Transaction>();

        Transaction Tx(Account acct, Payee? payee, Category? cat, DateOnly date, decimal amount, string? memo = null)
        {
            if (date > today.AddDays(-1)) date = today.AddDays(-1);
            return new Transaction
            {
                AccountId   = acct.Id,
                PayeeId     = payee?.Id,
                CategoryId  = cat?.Id,
                Date        = date,
                Amount      = amount,
                MemoEncrypted = memo is null ? null : encryption.Encrypt(memo, dek),
                Status      = date < today.AddDays(-3) ? TransactionStatus.Cleared : TransactionStatus.Uncleared,
                CreatedAt   = DateTime.UtcNow,
            };
        }

        for (int monthsBack = 6; monthsBack >= 1; monthsBack--)
        {
            var refDate     = today.AddMonths(-monthsBack);
            var monthStart  = new DateOnly(refDate.Year, refDate.Month, 1);
            var daysInMonth = DateTime.DaysInMonth(refDate.Year, refDate.Month);
            var monthEnd    = new DateOnly(refDate.Year, refDate.Month, daysInMonth);

            DateOnly D(int day) => new(monthStart.Year, monthStart.Month, Math.Clamp(day, 1, daysInMonth));

            // Salary — 1st and 15th
            txList.Add(Tx(checking, payEmployer, subSalary, D(1),  +2_800m));
            txList.Add(Tx(checking, payEmployer, subSalary, D(15), +2_800m));

            // Mortgage (1st)
            txList.Add(Tx(checking, payWFMortgage, subMortgage, D(1), -1_650m));
            // Mortgage principal payment on loan
            txList.Add(Tx(mortgage, payWFMortgage, subMortgage, D(1), +650m, "Principal"));

            // Car loan (15th)
            txList.Add(Tx(checking, payHonda, null, D(15), -285m, "Car loan payment"));
            txList.Add(Tx(carLoan,  payHonda, null, D(15), +95m,  "Principal"));

            // Home insurance (1st)
            txList.Add(Tx(checking, payStateFarm, subHomeIns, D(1), -142m));

            // Car insurance (8th) — from CC
            txList.Add(Tx(cc, payGeicoCarIns, subCarIns, D(8), -114m));

            // Streaming (1st) — CC
            txList.Add(Tx(cc, payNetflix,   subStream, D(1), -15.99m));
            txList.Add(Tx(cc, paySpotify,   subStream, D(1), -9.99m));
            txList.Add(Tx(cc, payPlanetFit, subGym,    D(2), -24.99m));

            // Electric (~5th)
            var electricAmt = 85m + rng.Next(0, 70);
            txList.Add(Tx(checking, payPGE, subElectric, D(5), -electricAmt));

            // Internet (10th)
            txList.Add(Tx(checking, payComcast, subInternet, D(10), -79.99m));

            // Phone (20th) — CC
            txList.Add(Tx(cc, payATT, subPhone, D(20), -85m));

            // Weekly groceries — CC
            for (int w = 0; w < 4; w++)
            {
                var grocDate = D(3 + w * 7);
                var grocPayee = (rng.Next(3)) switch { 0 => payCostco, 1 => payTraderJoes, _ => payWholefoods };
                var grocAmt = 85m + rng.Next(0, 110);
                txList.Add(Tx(cc, grocPayee, subGrocery, grocDate, -grocAmt));
            }

            // Gas — biweekly, CC
            txList.Add(Tx(cc, rng.Next(2) == 0 ? payShell : payChevron, subGas, D(6),  -(55m + rng.Next(0, 20))));
            txList.Add(Tx(cc, rng.Next(2) == 0 ? payShell : payChevron, subGas, D(20), -(55m + rng.Next(0, 20))));

            // Restaurants — 2-3 per month, CC
            var restPayees = new[] { payChipotle, payOlive, payTexasRoadhse };
            int restCount = 2 + rng.Next(2);
            for (int r = 0; r < restCount; r++)
            {
                var d = D(4 + rng.Next(22));
                txList.Add(Tx(cc, restPayees[rng.Next(restPayees.Length)], subRestaurant, d, -(25m + rng.Next(0, 65))));
            }

            // Coffee — weekly Starbucks, CC
            for (int w = 0; w < 4; w++)
                txList.Add(Tx(cc, payStarbucks, subCoffee, D(2 + w * 7), -(6.50m + rng.Next(0, 5))));

            // Amazon — 1-2 per month, CC
            for (int a = 0; a < 1 + rng.Next(2); a++)
                txList.Add(Tx(cc, payAmazon, subAmazon, D(rng.Next(1, 28)), -(22m + rng.Next(0, 120))));

            // Target — every other month
            if (monthsBack % 2 == 0)
                txList.Add(Tx(cc, payTarget, null, D(rng.Next(5, 25)), -(35m + rng.Next(0, 80))));

            // H&M — occasional clothing
            if (rng.Next(3) == 0)
                txList.Add(Tx(cc, payHnM, subClothing, D(rng.Next(5, 25)), -(40m + rng.Next(0, 80))));

            // Home Depot — occasional repair
            if (rng.Next(4) == 0)
                txList.Add(Tx(checking, payHomeDepot, subRepairs, D(rng.Next(5, 25)), -(45m + rng.Next(0, 200))));

            // Doctor/pharmacy — quarterly
            if (monthsBack % 3 == 0)
            {
                txList.Add(Tx(cc, payKaiser, subDoctor,   D(rng.Next(5, 20)), -(25m + rng.Next(0, 50))));
                txList.Add(Tx(cc, payCVS,    subPharmacy, D(rng.Next(5, 28)), -(12m + rng.Next(0, 30))));
            }

            // Movies — occasional
            if (rng.Next(3) == 0)
                txList.Add(Tx(cc, null, subMovies, D(rng.Next(5, 28)), -(18m + rng.Next(0, 25)), "Movie tickets"));

            // CC payment from checking (~25th)
            decimal ccPayment = 850m + rng.Next(0, 200);
            txList.Add(Tx(checking, null, null, D(25), -ccPayment, "Chase Sapphire payment"));
            txList.Add(Tx(cc,       null, null, D(26), +ccPayment, "Payment - thank you"));

            // Savings deposit from checking (~28th)
            txList.Add(Tx(checking, null, null, D(28), -500m, "Transfer to savings"));
            txList.Add(Tx(savings,  null, null, D(28), +500m, "Transfer from checking"));

            // Interest on savings (~last day)
            txList.Add(Tx(savings, null, subInterest, monthEnd, +(3m + rng.Next(0, 5)), "Interest"));
        }

        // Add a few transactions in the current month up to yesterday
        var curStart = new DateOnly(today.Year, today.Month, 1);
        if (today.Day > 1)
        {
            txList.Add(Tx(checking, payEmployer,  subSalary,    curStart,            +2_800m));
            txList.Add(Tx(checking, payWFMortgage, subMortgage, curStart,            -1_650m));
            txList.Add(Tx(checking, payStateFarm,  subHomeIns,  curStart,            -142m));
            txList.Add(Tx(cc,       payNetflix,    subStream,   curStart,            -15.99m));
            txList.Add(Tx(cc,       paySpotify,    subStream,   curStart,            -9.99m));
            if (today.Day > 4)
                txList.Add(Tx(checking, payPGE, subElectric, curStart.AddDays(3), -(85m + rng.Next(0, 70))));
            if (today.Day > 5)
                txList.Add(Tx(cc, payWholefoods, subGrocery, curStart.AddDays(4), -(90m + rng.Next(0, 80))));
            if (today.Day > 7)
                txList.Add(Tx(cc, payStarbucks, subCoffee, curStart.AddDays(6), -(8m + rng.Next(0, 4))));
        }

        db.Transactions.AddRange(txList);
        await db.SaveChangesAsync();

        // ── Scheduled Transactions ─────────────────────────────────────────────

        DateOnly NextOccurrence(int dayOfMonth)
        {
            var candidate = new DateOnly(today.Year, today.Month, Math.Min(dayOfMonth, DateTime.DaysInMonth(today.Year, today.Month)));
            return candidate <= today ? candidate.AddMonths(1) : candidate;
        }
        DateOnly NextBiweekly(DateOnly last) => last.AddDays(14) > today ? last.AddDays(14) : last.AddDays(14);

        var scheduled = new List<ScheduledTransaction>
        {
            new() {
                UserId = user.Id, Name = "Paycheck", AccountId = checking.Id,
                PayeeId = payEmployer.Id, CategoryId = subSalary.Id,
                Amount = +2_800m, FrequencyUnit = FrequencyUnit.Weeks, FrequencyInterval = 2,
                NextDueDate = NextBiweekly(new DateOnly(today.Year, today.Month, 15)),
                ReminderDays = 0, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Mortgage Payment", AccountId = checking.Id,
                PayeeId = payWFMortgage.Id, CategoryId = subMortgage.Id,
                Amount = -1_650m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(1), ReminderDays = 3, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Car Loan", AccountId = checking.Id,
                PayeeId = payHonda.Id, CategoryId = null,
                Amount = -285m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(15), ReminderDays = 2, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Electric Bill (PG&E)", AccountId = checking.Id,
                PayeeId = payPGE.Id, CategoryId = subElectric.Id,
                Amount = -110m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(5), ReminderDays = 2, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Internet (Comcast)", AccountId = checking.Id,
                PayeeId = payComcast.Id, CategoryId = subInternet.Id,
                Amount = -79.99m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(10), ReminderDays = 1, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Netflix", AccountId = cc.Id,
                PayeeId = payNetflix.Id, CategoryId = subStream.Id,
                Amount = -15.99m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(1), ReminderDays = 0, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Spotify", AccountId = cc.Id,
                PayeeId = paySpotify.Id, CategoryId = subStream.Id,
                Amount = -9.99m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(1), ReminderDays = 0, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "AT&T Phone", AccountId = cc.Id,
                PayeeId = payATT.Id, CategoryId = subPhone.Id,
                Amount = -85m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(20), ReminderDays = 1, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Planet Fitness", AccountId = cc.Id,
                PayeeId = payPlanetFit.Id, CategoryId = subGym.Id,
                Amount = -24.99m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(2), ReminderDays = 0, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Home Insurance", AccountId = checking.Id,
                PayeeId = payStateFarm.Id, CategoryId = subHomeIns.Id,
                Amount = -142m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(1), ReminderDays = 2, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Car Insurance (GEICO)", AccountId = cc.Id,
                PayeeId = payGeicoCarIns.Id, CategoryId = subCarIns.Id,
                Amount = -114m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(8), ReminderDays = 2, IsActive = true,
            },
            new() {
                UserId = user.Id, Name = "Savings Deposit", AccountId = checking.Id,
                PayeeId = null, CategoryId = null,
                Amount = -500m, FrequencyUnit = FrequencyUnit.Months, FrequencyInterval = 1,
                NextDueDate = NextOccurrence(28), ReminderDays = 0, IsActive = true,
                TransferAccountId = savings.Id,
            },
        };

        db.ScheduledTransactions.AddRange(scheduled);
        await db.SaveChangesAsync();
    }

    // suppress "payTarget has no category" warning
    private static Payee? subShop => null;
}
