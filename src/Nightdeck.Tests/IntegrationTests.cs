using Nightdeck.Core;
using Nightdeck.Mock;

namespace Nightdeck.Tests;

static class IntegrationTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        using var mock = new MockPlayer();
        mock.Start();
        await using var client = new BluOsClient("127.0.0.1", mock.Port);
        try
        {
            var status = await client.GetStatusAsync();
            TestHarness.Equal(status.State, "play", "mock is playing");
            TestHarness.Equal(status.Title, "Track 1", "mock title");
            TestHarness.Equal(status.Volume, 40, "mock volume");
            TestHarness.Equal(status.Repeat, 2, "mock repeat off");
            TestHarness.Check(status.Image.Contains("Artwork", StringComparison.Ordinal), "mock image path");

            var root = await client.BrowseAsync(BluOs.LibraryRoot);
            TestHarness.Equal(root.SearchKey, "LocalMusic:Search", "library search key");
            var artists = root.Items.First(i => i.Text == "Artists");
            var artistList = await client.BrowseAsync(artists.BrowseKey);
            TestHarness.Check(artistList.Items.Count >= 8, "artists listed");
            var albums = await client.BrowseAsync(artistList.Items[0].BrowseKey);
            TestHarness.Check(albums.Items.Count > 0 && albums.Items[0].IsContainer, "artist opens an album");
            var tracks = await client.BrowseAsync(albums.Items[0].BrowseKey);
            TestHarness.Check(tracks.Items.Count > 0 && tracks.Items[0].IsTrack, "album opens tracks");

            var found = await client.BrowseAsync(root.SearchKey, "alpha");
            var albumHits = await client.BrowseAsync(found.Items.First(i => i.Text == "Albums").BrowseKey);
            TestHarness.Check(albumHits.Items.Any(i => i.Text == "Alpha"), "search finds Alpha");

            int before = (await client.GetQueueAsync()).Songs.Count;
            int added = await client.AddItemAsync(albumHits.Items[0], playNow: false);
            TestHarness.Check(added >= 1, "add album reports a call");
            var queue = await client.GetQueueAsync();
            TestHarness.Check(queue.Songs.Count > before, "queue grew");

            await client.AddItemAsync(albumHits.Items[0], playNow: true);
            var playing = await client.GetStatusAsync();
            TestHarness.Equal(playing.State, "play", "play now is playing");
            TestHarness.Check(playing.Title.Contains("Alpha", StringComparison.Ordinal), "play now landed on the album");

            string etag = playing.Etag;
            var poll = client.PollStatusAsync(etag, 15);
            await client.PauseToggleAsync();
            var paused = await poll.WaitAsync(TimeSpan.FromSeconds(3));
            TestHarness.Equal(paused.State, "pause", "long-poll wakes on pause");

            await client.SeekAsync(50);
            var sought = await client.GetStatusAsync();
            TestHarness.Check(sought.Secs >= 50 && sought.Secs < 55, "seek lands near 50 (got " + sought.Secs + ")");

            await client.SetVolumeAsync(10);
            TestHarness.Equal((await client.GetStatusAsync()).Volume, 10, "volume level");
            await client.SetShuffleAsync(true);
            await client.SetRepeatAsync(BluOs.RepeatQueue);
            var flags = await client.GetStatusAsync();
            TestHarness.Equal(flags.Shuffle, true, "shuffle on");
            TestHarness.Equal(flags.Repeat, BluOs.RepeatQueue, "repeat queue");

            var beforeMove = await client.GetQueueAsync();
            string first = beforeMove.Songs[0].Title;
            string second = beforeMove.Songs[1].Title;
            await client.MoveSongAsync(0, 1);
            var moved = await client.GetQueueAsync();
            TestHarness.Equal(moved.Songs[0].Title, second, "move swapped the first title");
            TestHarness.Equal(moved.Songs[1].Title, first, "move swapped the second title");

            int count = moved.Songs.Count;
            await client.DeleteSongAsync(0);
            TestHarness.Equal((await client.GetQueueAsync()).Songs.Count, count - 1, "delete shrinks the queue");
            await client.ClearQueueAsync();
            TestHarness.Equal((await client.GetQueueAsync()).Songs.Count, 0, "clear empties the queue");

            TestHarness.Equal(await client.StepSleepAsync(), 15, "sleep steps to 15");

            var presets = await client.GetPresetsAsync();
            TestHarness.Equal(presets.Count, 2, "two presets");
            await client.PlayPresetAsync(presets[0].Id);
            TestHarness.Equal((await client.GetQueueAsync()).Songs[0].Title, "Radio One", "preset replaced the queue");

            await client.SavePlaylistAsync("Night");
            var lists = await client.ListPlaylistsAsync();
            var saved = lists.First(i => i.Text == "Night");
            TestHarness.Check(saved.PlayUrl.Contains("Night", StringComparison.Ordinal), "playlist play url");
            await client.ClearQueueAsync();
            await client.SendPathAsync(saved.PlayUrl);
            TestHarness.Equal((await client.GetQueueAsync()).Songs[0].Title, "Radio One", "load restores the playlist");
            var menu = await client.BrowseAllAsync(saved.ContextMenuKey);
            string? delete = menu.FirstOrDefault(i => i.Type == "delete")?.ActionUrl;
            TestHarness.Check(delete != null, "playlist has a delete action");
            await client.SendPathAsync(delete!);
            TestHarness.Check(!(await client.ListPlaylistsAsync()).Any(i => i.Text == "Night"), "playlist deleted");

            var art = await client.GetBytesAsync((await client.GetStatusAsync()).Image, 100_000);
            TestHarness.Check(art is { Length: >= 8 } && art[0] == 137 && art[1] == 80 && art[2] == 78 && art[3] == 71, "artwork png");
        }
        catch (Exception ex)
        {
            TestHarness.Check(false, "integration threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
