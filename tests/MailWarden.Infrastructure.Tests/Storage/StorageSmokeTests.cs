namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
[Trait("Category", "smoke")]
public sealed class StorageSmokeTests
{
    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task File_database_and_eml_store_work_end_to_end_and_release_all_handles()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "mailwarden-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var paths = new DataPaths(rootPath);
            byte[] emlBytes = "From: sender_a@example.test\r\nSubject: sample_subject_invoice\r\n\r\nbody\r\n"u8.ToArray();

            await using (SqliteDatabase database = SqliteDatabase.ForFile(paths.DatabasePath))
            {
                // Database directory must exist for ForFile; the root was created above.
                await database.InitializeAsync();
                var accounts = new SqliteAccountStore(database);
                var folders = new SqliteFolderStore(database);
                var messages = new SqliteMessageStore(database);
                var emlStore = new FileEmlStore(paths);

                Account account = TestData.NewAccount("Account_A", "sender_a@example.test");
                await accounts.UpsertAsync(account);
                MailFolder inbox = await folders.UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));

                string emlPath = emlStore.RelativePathFor(account.Id, inbox.Id, 1);
                await emlStore.WriteAsync(emlPath, new MemoryStream(emlBytes));
                await messages.UpsertAsync(
                    TestData.NewMessage(account.Id, inbox.Id, 1, subject: "sample_subject_invoice", emlPath: emlPath),
                    "body");

                IReadOnlyList<MailMessage> inboxMessages = await messages.ListAsync(new MessageQuery(Role: FolderRole.Inbox));
                MailMessage listed = Assert.Single(inboxMessages);
                Assert.Equal(emlPath, listed.EmlPath);

                await using Stream stream = await emlStore.OpenReadAsync(listed.EmlPath!);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                Assert.Equal(emlBytes, buffer.ToArray());
            }

            // All handles must be released: the whole tree is removable now (throws IOException otherwise).
            Directory.Delete(rootPath, recursive: true);
            Assert.False(Directory.Exists(rootPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
