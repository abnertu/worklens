using WorkLens.Core.Security;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class SlideDisclosureTests
{
    [Theory]
    // URL with a scheme, with user info and port.
    [InlineData("Fix login https://git.example.com/a/b/-/merge_requests/3", "Fix login")]
    [InlineData("Proxy https://user@host:8443/x setup", "Proxy setup")]
    // SSH remote; the sentence period stays.
    [InlineData("Mirror git@gitlab.example.com:org/repo.git now", "Mirror now")]
    [InlineData("Push to git@host:org/repo.git.", "Push to .")]
    // URLs without a scheme.
    [InlineData("See www.example.com/docs for it", "See for it")]
    // www. needs no path; only the www. rule removes this.
    [InlineData("See www.example.com today", "See today")]
    [InlineData("See wiki.example.org/page, then retry", "See , then retry")]
    // Email, with and without mailto.
    [InlineData("Ask a.b@example.com.", "Ask .")]
    [InlineData("Write mailto:a@b.com today", "Write today")]
    // Commit hashes of 7 and 40 characters, in any case.
    [InlineData("Revert abc1234 again", "Revert again")]
    [InlineData("Revert 0123456789ABCDEF0123456789abcdef01234567", "Revert")]
    // CJK characters end a match without a space.
    [InlineData("修正https://x.com/a的登入問題", "修正的登入問題")]
    [InlineData("修正abc1234def的問題", "修正的問題")]
    [InlineData("聯絡a@b.com。", "聯絡。")]
    // Trailing punctuation goes back to the sentence.
    [InlineData("(see https://x.com/a).", "(see ).")]
    // Whitespace left by a removal collapses.
    [InlineData("  a   https://x.com   b  ", "a b")]
    public void Removes_links_addresses_and_hashes(string title, string expected) =>
        Assert.Equal(expected, SlideDisclosure.EvidenceTitle(title));

    [Theory]
    [InlineData("Release 20261114 build")]
    [InlineData("Remove deadbeef cafe")]
    [InlineData("Read v1.2/notes first")]
    [InlineData("Move to gitlab.example.com")]
    [InlineData("Ticket 1234567 closed")]
    // A hash must stand alone: an ASCII letter after the hex run keeps it.
    [InlineData("Build abc1234defg ok")]
    // Hashes have 7 to 40 characters. The 41-character run also shows that a hash cannot start inside a hex run.
    [InlineData("Short abc123 stays")]
    [InlineData("Long 0123456789abcdef0123456789abcdef012345678 stays")]
    // A URL without a scheme cannot start right after a dot, a dash, a letter or a digit.
    [InlineData("Path ..example.com/x stays")]
    // A top-level domain has 2 or more letters.
    [InlineData("Keep x.c/y and a@b.c")]
    public void Keeps_numbers_hex_words_and_hosts_without_a_path(string title) =>
        Assert.Equal(title, SlideDisclosure.EvidenceTitle(title));

    // The token goes first to the Redactor, so removing its hex parts cannot leave the rest of it on a slide.
    [Fact]
    public void Masks_a_gitlab_token_whole()
    {
        var token = "glpat-" + "abc1234def-5678abcd-ef90";

        Assert.Equal("Rotate *** today", SlideDisclosure.EvidenceTitle($"Rotate {token} today"));
    }

    [Theory]
    [InlineData("https://x.com/a")]
    [InlineData("   ")]
    [InlineData("")]
    public void Title_with_nothing_left_is_a_dash(string title) =>
        Assert.Equal("—", SlideDisclosure.EvidenceTitle(title));
}
