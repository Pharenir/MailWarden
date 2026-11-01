using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>
/// AP-06 fix.2 R4 / case 23 (Allowed needs a well-formed sender, Blocked stays tolerant) and R5 / case 24 (quoted local
/// part, Punycode and U-form domains). Every refusal has a positive control.
/// </summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:contacts")]
public sealed class ContactSenderDirectoryHardeningTests
{
    private static SenderListEntry Address(string entry, ListMembership kind) => new(entry, false, kind);

    private static SenderListEntry Domain(string entry, ListMembership kind) => new(entry, true, kind);

    private static ContactSenderDirectory Create(params SenderListEntry[] lists) => new([], [], lists);

    // Case 23: Kelvin sign (U+212A lowercases to 'k')
    [Fact]
    public void Allowed_domain_does_not_match_a_sender_with_a_kelvin_sign_but_blocked_does()
    {
        const string kelvinSender = "sender_a@Kelvin.test";
        ContactSenderDirectory allowed = Create(Domain("kelvin.test", ListMembership.Allowed));
        ContactSenderDirectory blocked = Create(Domain("kelvin.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.None, allowed.GetListMembership(kelvinSender));
        Assert.Equal(ListMembership.Blocked, blocked.GetListMembership(kelvinSender));
    }

    // Case 23: control, ASCII case differences are fine for Allowed
    [Theory]
    [InlineData("sender_a@kelvin.test")]
    [InlineData("sender_a@KELVIN.TEST")]
    [InlineData("Sender_A@Kelvin.Test")]
    public void Allowed_domain_matches_ascii_case_variants(string sender)
    {
        ContactSenderDirectory directory = Create(Domain("kelvin.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.Allowed, directory.GetListMembership(sender));
    }

    // Case 23: trailing no-break space
    [Fact]
    public void Allowed_does_not_match_a_domain_with_a_trailing_no_break_space_but_blocked_does()
    {
        const string nbspSender = "sender_a@kelvin.test ";
        ContactSenderDirectory allowedDomain = Create(Domain("kelvin.test", ListMembership.Allowed));
        ContactSenderDirectory allowedAddress = Create(Address("sender_a@kelvin.test", ListMembership.Allowed));
        ContactSenderDirectory blocked = Create(Domain("kelvin.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.None, allowedDomain.GetListMembership(nbspSender));
        Assert.Equal(ListMembership.None, allowedAddress.GetListMembership(nbspSender));
        Assert.Equal(ListMembership.Blocked, blocked.GetListMembership(nbspSender));
    }

    // Case 24: a fully quoted local part matches the unquoted entry
    [Fact]
    public void Quoted_local_part_matches_the_address_entry()
    {
        ContactSenderDirectory directory = Create(Address("spam@addr.example.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("\"spam\"@addr.example.test"));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("spam@addr.example.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("\"other\"@addr.example.test"));
    }

    // Case 24 (fix.3): a quoted local part is not an exact sender, so it never hits an allow entry; the unquoted one does
    [Fact]
    public void Quoted_local_part_never_hits_an_allow_entry()
    {
        ContactSenderDirectory directory = Create(Address("friend@x.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.None, directory.GetListMembership("\"friend\"@x.test"));
        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("friend@x.test"));
    }

    // Case 27 (T1): look-alike characters are not an exact sender: no Allowed, Blocked still matches
    [Theory]
    [InlineData("sender_a@ｅxample.test")]
    [InlineData("sender_a@example。test")]
    [InlineData("person_a@ａllowaddr.test")]
    [InlineData("sender_a@\U0001D41Axample.test")]
    [InlineData("person_a@\U0001D41Allowaddr.test")]
    public void Look_alike_senders_never_hit_allow_entries_but_still_hit_block_entries(string sender)
    {
        ContactSenderDirectory allowed = Create(
            Domain("example.test", ListMembership.Allowed), Address("person_a@allowaddr.test", ListMembership.Allowed));
        ContactSenderDirectory blocked = Create(
            Domain("example.test", ListMembership.Blocked), Address("person_a@allowaddr.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.None, allowed.GetListMembership(sender));
        Assert.Equal(ListMembership.Blocked, blocked.GetListMembership(sender));
    }

    // Case 27: positive controls with the same entries
    [Fact]
    public void Plain_ascii_and_real_punycode_senders_are_allowed()
    {
        ContactSenderDirectory directory = Create(
            Domain("example.test", ListMembership.Allowed),
            Address("person_a@allowaddr.test", ListMembership.Allowed),
            Domain("bücher.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_a@example.test"));
        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("person_a@allowaddr.test"));
        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_a@xn--bcher-kva.test"));
    }

    // Case 28 (T2): an unusual label does not hide a blocked parent
    [Theory]
    [InlineData("sender_a@sub.xn--mller-kva.example")]
    [InlineData("sender_a@ab--cd.xn--mller-kva.example")]
    [InlineData("sender_a@xn--mller-kva.example")]
    public void Unusual_label_below_a_blocked_domain_is_still_blocked(string sender)
    {
        ContactSenderDirectory directory = Create(Domain("müller.example", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership(sender));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@other.example"));
    }

    // Case 24: Punycode entry, U-form sender, and the other way round
    [Fact]
    public void Punycode_entry_matches_the_u_form_sender()
    {
        ContactSenderDirectory directory = Create(Domain("xn--bcher-kva.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@bücher.test"));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@xn--bcher-kva.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@bucher.test"));
    }

    [Fact]
    public void U_form_entry_matches_the_punycode_sender()
    {
        ContactSenderDirectory directory = Create(Domain("bücher.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@xn--bcher-kva.test"));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@bücher.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@bucher.test"));
    }

    // Case 24: plus and dot variants stay exact (documented limit)
    [Theory]
    [InlineData("spam+tag@addr.example.test")]
    [InlineData("s.pam@addr.example.test")]
    public void Plus_and_dot_variants_of_an_address_do_not_match(string sender)
    {
        ContactSenderDirectory directory = Create(Address("spam@addr.example.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.None, directory.GetListMembership(sender));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("spam@addr.example.test"));
    }

    // Case 24: '*' is rejected by the store (positive control: the plain domain is accepted)
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("*.x.test", true)]
    [InlineData("x*.test", true)]
    [InlineData("*@x.test", false)]
    [InlineData("sender_*@x.test", false)]
    public async Task Entry_with_an_asterisk_is_rejected_by_the_store(string entry, bool isDomain)
    {
        await using StorageFixture storage = await StorageFixture.CreateAsync();
        var store = new SqliteSenderListStore(storage.Database);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(new SenderListEntry(entry, isDomain, ListMembership.Blocked)));
        await store.SetAsync(new SenderListEntry("x.test", true, ListMembership.Blocked));

        Assert.Single(await store.ListAsync());
    }
}
