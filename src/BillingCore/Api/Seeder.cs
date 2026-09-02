using BillingCore.Domain;
using BillingCore.Infrastructure;
using BillingCore.Services;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Api;

/// <summary>
/// Idempotent demo seed: studios + members/instruments. Token names carry gateway behavior;
/// account balances are initialized on the commerce WireMock ledger (HLD 5c).
/// </summary>
public static class Seeder
{
    private record MemberSpec(string Key, int StudioId, string Token, string Kind, string FundingType, long BalanceCents);

    private static readonly MemberSpec[] Specs =
    {
        new("d1", 1, "tok_ok_d1", "card", "credit", 0),
        new("d3soft", 1, "tok_softok_d3soft", "card", "credit", 0),
        new("d3hard", 1, "tok_hard_d3hard", "card", "credit", 0),
        new("d3fix", 1, "tok_fix_d3fix", "card", "credit", 0),
        new("d4cap", 1, "tok_ok_d4cap", "card", "credit", 5000),
        new("d4fb", 1, "tok_softalways_d4fb", "card", "credit", 3000),
        new("d4acct", 1, "tok_unused_d4acct", "card", "credit", 10000),
        new("d5debit", 1, "tok_debit_d5debit", "card", "debit", 0),
        new("d5outage", 1, "tok_ok_d5outage", "card", "credit", 0),
        new("d5swap", 1, "tok_softalways_d5swap", "card", "credit", 0),
        new("d5unknown", 1, "tok_unknown_d5unknown", "card", "unknown", 0),
        new("d6", 1, "tok_timeout_d6", "card", "credit", 0),
        new("d7", 1, "tok_ach_d7", "bank", "n/a", 0),
        new("d7poll", 1, "tok_ach_d7poll", "bank", "n/a", 0),
        new("s2", 2, "tok_ok_s2", "card", "credit", 0),
        new("d9", 3, "tok_ok_d9", "card", "credit", 0),
        new("d10", 1, "tok_ok_d10", "card", "credit", 0),
        new("d11", 1, "tok_ok_d11", "card", "credit", 0),
        new("d12", 1, "tok_ok_d12", "card", "credit", 0)
    };

    public static async Task<object> SeedAsync(IDbContextFactory<BillingDb> dbf, IClock clock, ExternalsClient? ext = null)
    {
        // Wipe WireMock journals / charge counters so DemoRunner re-runs are deterministic.
        if (ext is not null)
            await ext.ResetAsync();

        await using var db = await dbf.CreateDbContextAsync();

        if (!await db.Policies.AnyAsync(p => p.Id == "standard"))
        {
            var policy = new PolicyDef();
            PolicyDef.ValidateOrThrow(policy);
            db.Policies.Add(new Policy { Id = "standard", DefinitionJson = Json.Serialize(policy) });
        }

        var studios = new[]
        {
            new Studio { Id = 1, Name = "Alpha Fit (TF opted-in)" },
            new Studio { Id = 2, Name = "Beta Yoga (not TF opted-in)" },
            new Studio { Id = 3, Name = "Gamma Pilates" }
        };
        foreach (var studio in studios)
        {
            var existing = await db.Studios.SingleOrDefaultAsync(s => s.Id == studio.Id);
            if (existing is null) db.Studios.Add(studio);
            else existing.Paused = false; // clear ops pause left by prior demo runs
        }

        if (clock is VirtualClock vc)
            vc.Reset(); // prior demo advances must not leak across DemoRunner invocations

        var members = new Dictionary<string, object>();
        foreach (var spec in Specs)
        {
            var instrument = await db.Instruments.SingleOrDefaultAsync(i => i.Token == spec.Token);
            Member member;
            if (instrument is null)
            {
                member = new Member
                {
                    Id = Guid.NewGuid(),
                    StudioId = spec.StudioId,
                    Name = $"Member {spec.Key}",
                    Email = $"{spec.Key}@demo.local",
                    AccountBalanceCacheCents = spec.BalanceCents
                };
                db.Members.Add(member);
                instrument = new Instrument
                {
                    Id = Guid.NewGuid(),
                    MemberId = member.Id,
                    Kind = spec.Kind,
                    Token = spec.Token,
                    Brand = spec.Kind == "card" ? "visa" : null,
                    Last4 = "4242",
                    FundingType = spec.FundingType
                };
                db.Instruments.Add(instrument);
            }
            else
            {
                member = await db.Members.SingleAsync(m => m.Id == instrument.MemberId);
                member.AccountBalanceCacheCents = spec.BalanceCents;
            }

            if (ext is not null)
                await ext.SetAccountBalanceAsync(member.Id, spec.BalanceCents);

            members[spec.Key] = new
            {
                memberId = member.Id,
                instrumentId = instrument.Id,
                token = instrument.Token,
                studioId = member.StudioId
            };
        }

        await db.SaveChangesAsync();
        return new { utcNow = clock.UtcNow, studios, members };
    }
}
