using MailWarden.Core.Commands;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Assertions on queue contents shared by the classification tests.</summary>
internal static class CommandAssert
{
    /// <summary>Asserts that exactly one Move command targets <paramref name="target"/> and returns it.</summary>
    public static MailCommand SingleMoveTo(IEnumerable<MailCommand> commands, MailFolder target) =>
        Assert.Single(commands.Where(command => command.Kind == MailCommandKind.Move && command.TargetFolderId == target.Id));

    /// <summary>Asserts that no Move command targets <paramref name="target"/>.</summary>
    public static void NoMoveTo(IEnumerable<MailCommand> commands, MailFolder target) =>
        Assert.DoesNotContain(commands, command => command.Kind == MailCommandKind.Move && command.TargetFolderId == target.Id);

    /// <summary>Asserts that no command in the list deletes anything.</summary>
    public static void NoDeletes(IEnumerable<MailCommand> commands) =>
        Assert.DoesNotContain(commands, command =>
            command.Kind == MailCommandKind.DeletePermanently || command.Kind == MailCommandKind.MoveToTrash);

    /// <summary>All UIDs of the given commands in order.</summary>
    public static uint[] AllUids(IEnumerable<MailCommand> commands) => commands.SelectMany(command => command.Uids).ToArray();
}
