using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-06 case 4 / D3: allow and block lists in <see cref="ContactSenderDirectory"/>.</summary>
[Trait("Category", "feature:classifier")]
[Trait("Category", "feature:contacts")]
public sealed class ContactSenderDirectoryListTests
{
    private static SenderListEntry Address(string entry, ListMembership kind) => new(entry, false, kind);

    private static SenderListEntry Domain(string entry, ListMembership kind) => new(entry, true, kind);

    private static ContactSenderDirectory Create(params SenderListEntry[] lists) => new([], [], lists);

    // Case 4
    [Fact]
    [Trait("Category", "smoke")]
    public void Allowed_address_is_allowed_in_any_letter_case_and_others_are_not()
    {
        ContactSenderDirectory directory = Create(Address("Sender_A@Example.Test", ListMembership.Allowed));

        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_a@example.test"));
        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("SENDER_A@EXAMPLE.TEST"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_b@example.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@other.test"));
    }

    // Case 4: an address entry does not match by domain
    [Fact]
    public void Address_entry_does_not_cover_the_rest_of_its_domain()
    {
        ContactSenderDirectory directory = Create(Address("sender_a@example.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@example.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_b@example.test"));
    }

    // Case 4: domain blocked
    [Fact]
    public void Blocked_domain_blocks_every_address_of_the_domain_in_any_letter_case()
    {
        ContactSenderDirectory directory = Create(Domain("Blocked.Test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@blocked.test"));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("Sender_B@BLOCKED.TEST"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@other.test"));
    }

    // Case 4: sub-domain of a blocked domain (organisational domain)
    [Theory]
    [InlineData("sender_a@mail.blocked.test")]
    [InlineData("sender_a@news.mail.blocked.test")]
    public void Sub_domain_of_a_blocked_domain_is_blocked(string sender)
    {
        ContactSenderDirectory directory = Create(Domain("blocked.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership(sender));
    }

    // Case 4: lookalikes are not sub-domains
    [Theory]
    [InlineData("sender_a@notblocked.test")]
    [InlineData("sender_a@blocked.test.other")]
    [InlineData("sender_a@blocked.testx")]
    [InlineData("sender_a@blocked.other")]
    public void Lookalike_domains_of_a_blocked_domain_are_not_blocked(string sender)
    {
        ContactSenderDirectory directory = Create(Domain("blocked.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.None, directory.GetListMembership(sender));
    }

    // Case 4: organisational domain for multi-label suffixes, as DomainNames computes it
    [Fact]
    public void Domain_entry_matches_the_organisational_domain_below_a_multi_label_suffix()
    {
        ContactSenderDirectory directory = Create(Domain("example.co.uk", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@shop.example.co.uk"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@other.co.uk"));
    }

    // Case 4 (decision): a domain entry covers the domain and its sub-domains, never upward
    [Fact]
    public void Sub_domain_entry_does_not_match_upward_but_matches_below()
    {
        ContactSenderDirectory directory = Create(Domain("mail.blocked.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@mail.blocked.test"));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@news.mail.blocked.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@blocked.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("sender_a@other.blocked.test"));
    }

    // Case 4: a domain entry on the allow list
    [Fact]
    public void Allowed_domain_allows_its_sub_domains()
    {
        ContactSenderDirectory directory = Create(Domain("allowed.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_a@allowed.test"));
        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_a@mail.allowed.test"));
    }

    // Case 4: exact address wins over a domain entry, in both directions
    [Fact]
    public void Allowed_address_wins_over_a_blocked_domain()
    {
        ContactSenderDirectory directory = Create(
            Address("sender_a@blocked.test", ListMembership.Allowed),
            Domain("blocked.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_a@blocked.test"));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_b@blocked.test"));
    }

    // Case 4
    [Fact]
    public void Blocked_address_wins_over_an_allowed_domain()
    {
        ContactSenderDirectory directory = Create(
            Address("sender_a@allowed.test", ListMembership.Blocked),
            Domain("allowed.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@allowed.test"));
        Assert.Equal(ListMembership.Allowed, directory.GetListMembership("sender_b@allowed.test"));
    }

    // Case 4: both kinds given for one domain (the directory gets a plain sequence) -> Blocked wins
    [Fact]
    public void Blocked_wins_when_a_domain_is_given_with_both_kinds_in_either_order()
    {
        ContactSenderDirectory allowedFirst = Create(
            Domain("both.test", ListMembership.Allowed), Domain("both.test", ListMembership.Blocked));
        ContactSenderDirectory blockedFirst = Create(
            Domain("both.test", ListMembership.Blocked), Domain("both.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.Blocked, allowedFirst.GetListMembership("sender_a@both.test"));
        Assert.Equal(ListMembership.Blocked, blockedFirst.GetListMembership("sender_a@both.test"));
        Assert.Equal(ListMembership.Blocked, allowedFirst.GetListMembership("sender_a@mail.both.test"));
    }

    // Case 4: the same for an address given with both kinds
    [Fact]
    public void Blocked_wins_when_an_address_is_given_with_both_kinds()
    {
        ContactSenderDirectory directory = Create(
            Address("sender_a@example.test", ListMembership.Allowed), Address("sender_a@example.test", ListMembership.Blocked));

        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_a@example.test"));
    }

    // Case 4: malformed addresses -> None, with a positive control in the same directory
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_at_sign")]
    [InlineData("sender_a@")]
    [InlineData("@blocked.test.")]
    [InlineData("sender_a@...")]
    public void Blank_or_malformed_addresses_give_none(string address)
    {
        ContactSenderDirectory directory = Create(
            Domain("blocked.test", ListMembership.Blocked), Address("sender_a@example.test", ListMembership.Allowed));

        Assert.Equal(ListMembership.None, directory.GetListMembership(address));
        Assert.Equal(ListMembership.Blocked, directory.GetListMembership("sender_x@blocked.test"));
    }

    // Case 4: lists do not make a contact, contacts do not make a list member
    [Fact]
    public void Lists_and_contacts_are_independent()
    {
        var directory = new ContactSenderDirectory(
            ["contact_a@example.test"], ["own_a@example.test"], [Address("listed_a@example.test", ListMembership.Allowed)]);

        Assert.False(directory.IsContact("listed_a@example.test"));
        Assert.True(directory.IsContact("contact_a@example.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("contact_a@example.test"));
        Assert.Equal(ListMembership.None, directory.GetListMembership("own_a@example.test"));
        Assert.Equal(1, directory.ContactCount);
    }

    // Case 4: the old constructor stays and has no lists
    [Fact]
    public void Old_constructor_gives_empty_lists()
    {
        var directory = new ContactSenderDirectory(["contact_a@example.test"], []);

        Assert.Equal(ListMembership.None, directory.GetListMembership("contact_a@example.test"));
    }
}
