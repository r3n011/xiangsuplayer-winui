using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelPlayer.WinUI.Models;
using PixelPlayer.WinUI.Services;

namespace PixelPlayer.WinUI.Pages;

/// <summary>媒体库：歌曲 / 专辑 / 艺术家 / 歌单 / 文件夹 / 历史 六标签（对应参照项目 LibraryScreen）。</summary>
public sealed partial class LibraryPage : Page
{
    private List<Track> _songs = new();
    private List<Track> _artistTracks = new();
    private List<Track> _playlistTracks = new();
    private List<Track> _folderTracks = new();

    private bool _suppressSortInit;

    public LibraryPage()
    {
        InitializeComponent();

        SortBox.SelectedIndex = 0;
        SortBox.SelectionChanged += Sort_Changed;   // 必须在设完默认值之后挂，避免初始化期回调

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged += OnLibraryChanged;
        HistoryService.Instance.HistoryChanged += OnHistoryChanged;

        await LibraryService.Instance.LoadCacheAsync();
        await HistoryService.Instance.LoadAsync();
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LibraryService.Instance.LibraryChanged -= OnLibraryChanged;
        HistoryService.Instance.HistoryChanged -= OnHistoryChanged;
    }

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshAll);

    private void OnHistoryChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsTab("history")) RefreshHistory();
        });

    private void RefreshAll()
    {
        RefreshSongs();
        RefreshAlbums();
        RefreshArtists();
        RefreshPlaylists();
        RefreshFolders();
        RefreshHistory();
    }

    /* ---------------- 标签切换 ---------------- */

    private bool IsTab(string tag) =>
        (TabBar.SelectedItem as SelectorBarItem)?.Tag as string == tag;

    private void Tab_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        SongsPanel.Visibility = IsTab("songs") ? Visibility.Visible : Visibility.Collapsed;
        AlbumGrid.Visibility = IsTab("albums") ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = IsTab("artists") ? Visibility.Visible : Visibility.Collapsed;
        PlaylistsPanel.Visibility = IsTab("playlists") ? Visibility.Visible : Visibility.Collapsed;
        FoldersPanel.Visibility = IsTab("folders") ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = IsTab("history") ? Visibility.Visible : Visibility.Collapsed;

        if (IsTab("folders")) RefreshFolders();
        if (IsTab("history")) RefreshHistory();
    }

    private Track[] CurrentTabTracks()
    {
        var tab = (TabBar.SelectedItem as SelectorBarItem)?.Tag as string;
        return tab switch
        {
            "albums" when AlbumGrid.SelectedItem is AlbumGroup album => album.Tracks.ToArray(),
            "artists" when _artistTracks.Count > 0 => _artistTracks.ToArray(),
            "playlists" when _playlistTracks.Count > 0 => _playlistTracks.ToArray(),
            "folders" when _folderTracks.Count > 0 => _folderTracks.ToArray(),
            "history" => HistoryService.Instance.Entries
                .Select(h => LibraryService.Instance.Tracks.FirstOrDefault(t => t.Path == h.Path))
                .Where(t => t is not null)
                .Cast<Track>()
                .ToArray(),
            _ => _songs.ToArray(),
        };
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        var tracks = CurrentTabTracks();
        if (tracks.Length > 0) PlaybackService.Instance.PlayQueue(tracks);
    }

    private void ShuffleAll_Click(object sender, RoutedEventArgs e)
    {
        var tracks = CurrentTabTracks();
        if (tracks.Length == 0) return;
        PlaybackService.Instance.PlayQueue(tracks.OrderBy(_ => Random.Shared.Next()));
    }

    /* ---------------- 歌曲 ---------------- */

    private void RefreshSongs()
    {
        var library = LibraryService.Instance;
        IEnumerable<Track> query = library.Search(SearchBox.Text ?? "", int.MaxValue);

        query = SortBox.SelectedIndex switch
        {
            1 => query.OrderBy(t => t.Artist).ThenBy(t => t.Title),
            2 => query.OrderBy(t => t.Album).ThenBy(t => t.TrackNumber),
            3 => query.OrderBy(t => t.DurationSeconds),
            _ => query.OrderBy(t => t.Title),
        };

        _songs = query.ToList();
        foreach (var track in _songs.Where(t => t.CoverThumb is null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 80);
        }

        TrackList.ItemsSource = _songs;
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) RefreshSongs();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e) => RefreshSongs();

    private void Track_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Track track) return;
        PlaybackService.Instance.PlayQueue(_songs, _songs.IndexOf(track));
    }

    /* ---------------- 专辑 ---------------- */

    private void RefreshAlbums()
    {
        AlbumGrid.ItemsSource = LibraryService.Instance.Albums;
    }

    private void Album_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AlbumGroup album || album.Tracks.Count == 0) return;
        PlaybackService.Instance.PlayQueue(album.Tracks);
    }

    /* ---------------- 艺术家 ---------------- */

    private void RefreshArtists()
    {
        ArtistList.ItemsSource = LibraryService.Instance.Artists;
    }

    private void Artist_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ArtistGroup artist) return;

        _artistTracks = artist.Tracks;
        foreach (var track in _artistTracks.Where(t => t.CoverThumb is null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 80);
        }

        ArtistDetailTitle.Text = artist.Name;
        ArtistTrackList.ItemsSource = _artistTracks;
        ArtistDetail.Visibility = Visibility.Visible;
    }

    private void ArtistBack_Click(object sender, RoutedEventArgs e) =>
        ArtistDetail.Visibility = Visibility.Collapsed;

    private void ArtistTrack_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Track track) return;
        PlaybackService.Instance.PlayQueue(_artistTracks, _artistTracks.IndexOf(track));
    }

    /* ---------------- 歌单 ---------------- */

    private void RefreshPlaylists()
    {
        var playlists = SettingsService.Instance.Playlists;
        PlaylistList.ItemsSource = playlists;
        if (PlaylistList.SelectedItem is null && playlists.Count > 0)
        {
            ShowPlaylistDetail(playlists[0]);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "例如：深夜通勤", MinWidth = 260 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新建播放列表",
            Content = box,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = string.IsNullOrWhiteSpace(box.Text) ? "未命名播放列表" : box.Text.Trim();
        var playlist = new Playlist { Name = name };
        SettingsService.Instance.Playlists.Add(playlist);
        SettingsService.Instance.SavePlaylists();
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = SettingsService.Instance.Playlists;
        PlaylistList.SelectedItem = playlist;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        SettingsService.Instance.Playlists.Remove(playlist);
        SettingsService.Instance.SavePlaylists();
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = SettingsService.Instance.Playlists;
        DetailTitle.Text = "未选择播放列表";
        DetailSubtitle.Text = "在左侧选择，或新建一个";
        DetailList.ItemsSource = null;
    }

    private void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Playlist playlist) ShowPlaylistDetail(playlist);
    }

    private void ShowPlaylistDetail(Playlist playlist)
    {
        var library = LibraryService.Instance;
        _playlistTracks = playlist.TrackPaths
            .Select(path => library.Tracks.FirstOrDefault(t => t.Path == path))
            .Where(t => t is not null)
            .Cast<Track>()
            .ToList();

        foreach (var track in _playlistTracks.Where(t => t.CoverThumb is null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 80);
        }

        DetailTitle.Text = playlist.Name;
        DetailSubtitle.Text = $"{_playlistTracks.Count} 首 · 创建于 {playlist.CreatedAt:yyyy-MM-dd}";
        DetailList.ItemsSource = _playlistTracks;
    }

    private void PlayPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistTracks.Count == 0) return;
        PlaybackService.Instance.PlayQueue(_playlistTracks);
    }

    private void Detail_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Track track) return;
        PlaybackService.Instance.PlayQueue(_playlistTracks, _playlistTracks.IndexOf(track));
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        if (DetailList.SelectedItem is not Track track) return;

        playlist.TrackPaths.Remove(track.Path);
        SettingsService.Instance.SavePlaylists();
        ShowPlaylistDetail(playlist);
    }

    /* ---------------- 文件夹 ---------------- */

    private void RefreshFolders()
    {
        var nodes = LibraryService.Instance.GetFolderNodes()
            .Select(f => new FolderNode { Path = f.Path, Name = f.Name, CountText = $"{f.TrackCount} 首" })
            .ToList();
        FolderNodeList.ItemsSource = nodes;
    }

    private void FolderNode_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FolderNode node) return;

        _folderTracks = LibraryService.Instance.GetTracksInFolder(node.Path);
        foreach (var track in _folderTracks.Where(t => t.CoverThumb is null))
        {
            track.CoverThumb = LibraryService.CreateThumb(track.CoverPath, 80);
        }

        FolderDetailTitle.Text = node.Name;
        FolderTrackList.ItemsSource = _folderTracks;
        FolderDetail.Visibility = Visibility.Visible;
    }

    private void FolderBack_Click(object sender, RoutedEventArgs e) =>
        FolderDetail.Visibility = Visibility.Collapsed;

    private void FolderTrack_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Track track) return;
        PlaybackService.Instance.PlayQueue(_folderTracks, _folderTracks.IndexOf(track));
    }

    /* ---------------- 历史 ---------------- */

    private void RefreshHistory()
    {
        var entries = HistoryService.Instance.Entries.ToList();
        HistoryList.ItemsSource = entries;
        HistorySummary.Text = $"共 {entries.Count} 条播放记录";
    }

    private void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlayHistoryEntry entry) return;
        var track = LibraryService.Instance.Tracks.FirstOrDefault(t => t.Path == entry.Path);
        if (track is null) return;

        // 以历史上下文（最近播放顺序）作为队列
        var queue = HistoryService.Instance.Entries
            .Select(h => LibraryService.Instance.Tracks.FirstOrDefault(t => t.Path == h.Path))
            .Where(t => t is not null)
            .Cast<Track>()
            .ToList();
        PlaybackService.Instance.PlayQueue(queue, queue.IndexOf(track));
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => HistoryService.Instance.Clear();
}
