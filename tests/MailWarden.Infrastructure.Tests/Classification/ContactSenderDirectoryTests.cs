using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c D5 / case 10: <see cref="ContactSenderDirectory"/>.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ContactSenderDirectoryTests
{
    private static ContactSenderDirectory Create(string[] contacts, string[] own) => new(contacts, own);

    // Case 10
    [Fact]
    public void Contact_is_found_in_any_letter_case()
    {
        ContactSenderDirectory directory = Create(["Contact_A@Example.Test"], []);

        Assert.True(directory.IsContact("contact_a@example.test"));
        Assert.True(directory.IsContact("CONTACT_A@EXAMPLE.TEST"));
        Assert.True(directory.IsContact("Contact_A@Example.Test"));
    }

    // Case 10
    [Fact]
    public void Own_address_counts_as_contact_in_any_letter_case()
    {
        ContactSenderDirectory directory = Create([], ["Own_A@Example.Test"]);

        Assert.True(directory.IsContact("own_a@example.test"));
        Assert.True(directory.IsContact("OWN_A@EXAMPLE.TEST"));
    }

    [Theory]
    [InlineData("other_a@example.test")]
    [InlineData("contact_a@example.test.other")]
    [InlineData("xcontact_a@example.test")]
    [InlineData("contact_a@other.test")]
    [InlineData("example.test")]
    public void Matching_is_exact_not_by_prefix_suffix_or_domain(string address)
    {
        ContactSenderDirectory directory = Create(["contact_a@example.test"], ["own_a@example.test"]);

        Assert.False(directory.IsContact(address));
    }

    // Case 10
    [Fact]
    public void Contact_count_counts_contacts_only()
    {
        ContactSenderDirectory directory = Create(
            ["contact_a@example.test", "contact_b@example.test"],
            ["own_a@example.test", "own_b@example.test", "own_c@example.test"]);

        Assert.Equal(2, directory.ContactCount);
    }

    // Case 10
    [Fact]
    public void Only_own_addresses_give_a_contact_count_of_zero()
    {
        ContactSenderDirectory directory = Create([], ["own_a@example.test"]);

        Assert.Equal(0, directory.ContactCount);
        Assert.True(directory.IsContact("own_a@example.test"));
    }

    [Fact]
    public void Empty_directory_has_no_contacts()
    {
        ContactSenderDirectory directory = Create([], []);

        Assert.Equal(0, directory.ContactCount);
        Assert.False(directory.IsContact("contact_a@example.test"));
    }

    // Case 10: allow/block lists come with AP-06
    [Theory]
    [InlineData("contact_a@example.test")]
    [InlineData("own_a@example.test")]
    [InlineData("stranger_a@example.test")]
    public void List_membership_is_none_for_everybody(string address)
    {
        ContactSenderDirectory directory = Create(["contact_a@example.test"], ["own_a@example.test"]);

        Assert.Equal(ListMembership.None, directory.GetListMembership(address));
    }

    [Fact]
    [Trait("Category", "smoke")]
    public void Directory_works_as_the_sender_directory_of_the_classifier()
    {
        ISenderDirectory directory = Create(["sender_a@paypal.test"], []);
        var classifier = new SpamClassifier(ClassifierSettings.Default, directory, SpamClassifier.CreateDefaultSignals());
        var input = new ClassificationInput(
            "sender_a@paypal.test",
            "Sender A",
            null,
            ["mx.google.com; dmarc=pass header.from=paypal.test"],
            "Your paypal statement is ready.",
            []);

        Assert.Equal(SpamTier.TrustedContact, classifier.Classify(input).Tier);
    }
}
