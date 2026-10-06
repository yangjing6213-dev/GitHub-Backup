using System.Buffers.Binary;
using System.Text;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupShortcutTests
{
    // These paths are only strings: the tests never create an executable or a shortcut file.
    private const string DirectoryPath = @"Z:\Synthetic shortcut fixture\应用";
    private const string TargetPath = DirectoryPath + @"\GitHubBackup.exe";

    [TestMethod]
    public void Memory_roundtrip_preserves_a_missing_target_and_working_directory()
    {
        byte[] bytes = SetupShortcut.Create(TargetPath, DirectoryPath);

        Assert.IsTrue(bytes.Length is > 0 and <= 65536);
        byte[] before = bytes.ToArray();
        SetupShortcut.Validate(bytes, TargetPath, DirectoryPath);
        CollectionAssert.AreEqual(before, bytes, "Validation must not alter the serialized shortcut.");
    }

    [TestMethod]
    public void Different_target_is_rejected_without_resolving_or_launching_it()
    {
        byte[] bytes = SetupShortcut.Create(TargetPath, DirectoryPath);

        var error = Assert.ThrowsExactly<IOException>(() => SetupShortcut.Validate(bytes,
            DirectoryPath + @"\Other.exe", DirectoryPath));
        StringAssert.Contains(error.Message, "SETUP_SHORTCUT_TARGET_MISMATCH");
    }

    [TestMethod]
    public void Nonempty_arguments_in_the_serialized_shortcut_are_rejected()
    {
        byte[] bytes = AddArguments(SetupShortcut.Create(TargetPath, DirectoryPath), "--unexpected");

        var error = Assert.ThrowsExactly<IOException>(() => SetupShortcut.Validate(bytes, TargetPath, DirectoryPath));
        StringAssert.Contains(error.Message, "SETUP_SHORTCUT_ARGUMENTS_NOT_EMPTY");
    }

    [TestMethod]
    public void Different_working_directory_is_rejected()
    {
        byte[] bytes = SetupShortcut.Create(TargetPath, DirectoryPath);

        var error = Assert.ThrowsExactly<IOException>(() => SetupShortcut.Validate(bytes, TargetPath,
            @"Z:\Different synthetic directory"));
        StringAssert.Contains(error.Message, "SETUP_SHORTCUT_WORKING_DIRECTORY_MISMATCH");
    }

    [TestMethod]
    public void Empty_oversized_or_invalid_bytes_are_rejected()
    {
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), new byte[65537], new byte[76] })
            Assert.ThrowsExactly<IOException>(() => SetupShortcut.Validate(bytes, TargetPath, DirectoryPath));
    }

    [TestMethod]
    public void Paths_that_cannot_be_read_back_without_truncation_are_rejected()
    {
        string longPath = @"Z:\" + new string('a', 257);
        Assert.AreEqual(260, longPath.Length);
        Assert.ThrowsExactly<ArgumentException>(() => SetupShortcut.Create(longPath, DirectoryPath));
        Assert.ThrowsExactly<ArgumentException>(() => SetupShortcut.Create(TargetPath, longPath));
        byte[] bytes = SetupShortcut.Create(TargetPath, DirectoryPath);
        Assert.ThrowsExactly<ArgumentException>(() => SetupShortcut.Validate(bytes, longPath, DirectoryPath));
        Assert.ThrowsExactly<ArgumentException>(() => SetupShortcut.Validate(bytes, TargetPath, longPath));
    }

    [TestMethod]
    public void Maximum_supported_path_lengths_roundtrip_without_truncation()
    {
        string prefix = @"Z:\" + new string('a', 120) + @"\" + new string('b', 120) + @"\";
        string target = prefix + "GitHubBack.exe";
        string directory = prefix + new string('c', 14);
        Assert.AreEqual(259, target.Length);
        Assert.AreEqual(259, directory.Length);

        SetupShortcut.Validate(SetupShortcut.Create(target, directory), target, directory);
    }

    [TestMethod]
    [DataRow(ApartmentState.STA)]
    [DataRow(ApartmentState.MTA)]
    public void Repeated_memory_roundtrips_work_in_both_COM_apartments(ApartmentState apartment)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 8; i++)
                    SetupShortcut.Validate(SetupShortcut.Create(TargetPath, DirectoryPath), TargetPath, DirectoryPath);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(apartment);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The in-memory COM roundtrip did not return.");
        Assert.IsNull(failure, failure?.ToString());
    }

    private static byte[] AddArguments(byte[] bytes, string arguments)
    {
        // Insert the StringData argument field into a valid in-memory Shell Link.
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
        Assert.AreNotEqual(0u, flags & 0x80, "The fixture must use Unicode StringData.");
        Assert.AreEqual(0u, flags & 0x20, "The original fixture must have no argument field.");
        int offset = 76;
        if ((flags & 1) != 0)
            offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        if ((flags & 2) != 0)
            offset += checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        foreach (uint field in new[] { 4u, 8u, 16u })
            if ((flags & field) != 0)
                offset += 2 + 2 * BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

        byte[] value = Encoding.Unicode.GetBytes(arguments);
        byte[] modified = new byte[bytes.Length + 2 + value.Length];
        bytes.AsSpan(0, offset).CopyTo(modified);
        BinaryPrimitives.WriteUInt32LittleEndian(modified.AsSpan(20), flags | 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(modified.AsSpan(offset), checked((ushort)arguments.Length));
        value.CopyTo(modified, offset + 2);
        bytes.AsSpan(offset).CopyTo(modified.AsSpan(offset + 2 + value.Length));
        return modified;
    }
}
