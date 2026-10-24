using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// A selection page: game filter chips (<c>tabs</c>), a grid of content cards (<c>card-scroll</c>, <c>card-grid</c>),
    /// the info panel of the hovered or focused card (<c>info-panel</c>) and a one-line empty state (<c>empty</c>).
    /// An optional <c>showcase-area</c> frames the 3D turntable. Subclasses list, describe, pick and decorate the items.
    /// </summary>
    public abstract class CardGridScreen<T> : MenuScreen where T : ContentDefinition
    {
        [SerializeField, Tooltip("Game filter chip (Templates/Chip.uxml: a Button with class chip).")]
        protected VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Content card (Templates/PortraitCard.uxml or TrackCard.uxml).")]
        protected VisualTreeAsset cardTemplate;
        [SerializeField, Tooltip("Seconds between the cards' entry animations.")]
        protected float cardStep = 0.025f;
        [SerializeField, Tooltip("Entry animation delay of the first card, seconds.")]
        protected float cardFirst = 0.12f;
        [SerializeField, Tooltip("Scale the info panel pops from when it shows another card (1 = no pop).")]
        float infoPopScale = 1.03f;
        [SerializeField, Tooltip("Milliseconds after the pointer left a card before the info panel shows the focused card again. Moving straight onto the next card keeps the panel from flickering.")]
        int hoverRestoreMs = 120;

        const string AllGamesLabel = "All";
        const int InfoPopMs = 220;

        VisualElement grid, tabs, info, infoImage;
        Label infoName, infoGame;
        IVisualElementScheduledItem restore;
        bool filterShown;
        // Resting: the card the panel returns to (focused, else the selected or first one). Shown: the described card.
        T resting, shown;

        /// <summary>Every item the page lists (before the game filter), in display order.</summary>
        protected abstract IEnumerable<T> Items { get; }

        protected abstract string CardTag(T item);

        protected abstract Texture2D CardImage(T item);

        /// <summary>Fills the info panel with <paramref name="item"/> (its name, game line and <c>info-image</c> are already set).</summary>
        protected abstract void Describe(T item);

        protected abstract void Pick(T item);

        /// <summary>Queries the page's own elements and prepares <see cref="Items"/> (runs before the grid is filled).</summary>
        protected abstract void BindPage();

        protected virtual bool IsSelected(T item) => false;

        /// <summary>Game line of the info panel.</summary>
        protected virtual string InfoGame(T item) => UiText.GameName(item.SourceGame);

        /// <summary>After <see cref="Describe"/>: the info panel now shows <paramref name="item"/> (hovered or focused).</summary>
        protected virtual void OnShown(T item) { }

        protected virtual void OnCardSpawned(Button card, T item) { }

        /// <summary>The grid was emptied before it is filled again (first fill and every game filter change).</summary>
        protected virtual void OnGridCleared() { }

        protected override void Bind()
        {
            grid = Q<VisualElement>("card-grid");
            info = Q<VisualElement>("info-panel");
            tabs = Q<VisualElement>("tabs");
            infoName = Q<Label>("info-name");
            infoGame = Q<Label>("info-game");
            infoImage = Root.Q("info-image");
            resting = shown = null;
            BindPage();
            var empty = !Items.Any();
            ShowEmptyState(empty);
            if (empty)
            {
                FocusTarget = BindEmptyDownload() ?? Root.Q<Button>("back");
                Flow.SetShowcaseVisible(false);
                return;
            }
            KeepFocusedInView(Q<ScrollView>("card-scroll"), grid);
            grid.RegisterCallback<NavigationMoveEvent>(OnGridNavigate);
            tabs.RegisterCallback<NavigationMoveEvent>(OnTabsNavigate);
            restore = grid.schedule.Execute(() => Show(resting));
            restore.Pause();
            Root.Q("showcase-area")?.RegisterCallback<GeometryChangedEvent>(OnShowcaseAreaChanged);
            FillGameTabs();
            FocusTarget = FillGrid();
        }

        protected override void Unbind()
        {
            base.Unbind();
            restore?.Pause();
            restore = null;
            Flow.FrameShowcase(null);
        }

        void ShowEmptyState(bool empty)
        {
            var line = Q<VisualElement>("empty");
            if (line.parent == null) return;
            foreach (var child in line.parent.Children()) child.SetVisible(empty ? child == line : child != line);
        }

        // The download of the built-in mods while they are missing, null when the page has none.
        Button BindEmptyDownload()
        {
            var download = Root.Q<Button>("empty-download");
            if (download == null) return null;
            var missing = ModSources.BaseGamesMissing;
            download.SetVisible(missing);
            if (!missing) return null;
            download.clicked += () =>
            {
                Click();
                ModsScreen.OpenModsSection(Flow);
            };
            return download;
        }

        void OnShowcaseAreaChanged(GeometryChangedEvent e)
        {
            if (e.target is not VisualElement area || area.panel == null) return;
            var size = area.panel.visualTree.layout.size;
            var r = area.worldBound;
            Flow.FrameShowcase(r.width > 0f && r.height > 0f && size.x > 0f && size.y > 0f
                ? new Rect(r.x / size.x, 1f - r.yMax / size.y, r.width / size.x, r.height / size.y)
                : null);
        }

        void Show(T item)
        {
            if (item == shown) return;
            shown = item;
            info.SetVisible(item != null);
            if (item == null) return;
            infoName.text = item.DisplayName;
            infoGame.text = InfoGame(item);
            infoImage?.SetImage(CardImage(item));
            Describe(item);
            OnShown(item);
            if (infoPopScale != 1f) UiAnim.Pop(info, infoPopScale, InfoPopMs);
        }

        void OnCardFocused(T item)
        {
            restore.Pause();
            resting = item;
            Show(item);
        }

        void OnCardHovered(T item)
        {
            restore.Pause();
            Show(item);
        }

        // The panel's geometric navigation skips columns in the wrapped grid (focused cards are scaled, neighbours
        // overlap by the negative margins), so arrows and the stick move by grid index. Up from the first row goes to
        // the game filter, other moves out of the grid keep the focus (the panel would jump to an arbitrary card).
        void OnGridNavigate(NavigationMoveEvent e)
        {
            if (e.target is not VisualElement card || card.parent != grid) return;
            var index = grid.IndexOf(card);
            var columns = GridColumns();
            var next = e.direction switch
            {
                NavigationMoveEvent.Direction.Left => index - 1,
                NavigationMoveEvent.Direction.Right => index + 1,
                NavigationMoveEvent.Direction.Up => index - columns,
                NavigationMoveEvent.Direction.Down => DownFrom(index, columns),
                _ => -1
            };
            var target = next >= 0 && next < grid.childCount ? grid[next] : null;
            if (target == null && e.direction == NavigationMoveEvent.Direction.Up && filterShown && tabs.childCount > 0)
                target = tabs.Q(className: "selected") ?? tabs[0];
            Move(e, target);
        }

        void OnTabsNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Down || grid.childCount == 0) return;
            Move(e, grid.Q(className: "selected") ?? grid[0]);
        }

        static void Move(NavigationMoveEvent e, VisualElement target)
        {
            target?.Focus();
            e.StopPropagation();
            if (e.target is VisualElement from) from.focusController?.IgnoreEvent(e);
        }

        // A partial last row: down from a column it lacks goes to its last card.
        int DownFrom(int index, int columns)
        {
            var below = index + columns;
            if (below < grid.childCount) return below;
            var lastRowStart = (grid.childCount - 1) / columns * columns;
            return index < lastRowStart ? grid.childCount - 1 : -1;
        }

        int GridColumns()
        {
            var top = grid[0].layout.y;
            var columns = 1;
            while (columns < grid.childCount && Mathf.Approximately(grid[columns].layout.y, top)) columns++;
            return columns;
        }

        // The player's filter when this page lists that game, else null (all).
        string GameFilter => filterShown && Items.Any(i => i.SourceGame == Selection.gameFilter) ? Selection.gameFilter : null;

        void FillGameTabs()
        {
            var games = Items.Select(i => i.SourceGame).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
            filterShown = games.Count > 1;
            tabs.SetVisible(filterShown);
            if (!filterShown)
            {
                tabs.Clear();
                return;
            }
            var filters = new List<string> { null };
            filters.AddRange(games);
            var labels = filters.Select(f => f == null ? AllGamesLabel : UiText.GameName(f)).ToList();
            ChipGroup.Fill(tabs, chipTemplate, labels, filters.IndexOf(GameFilter), index =>
            {
                Click();
                Selection.gameFilter = filters[index];
                FocusLater(FillGrid());
            });
        }

        VisualElement FillGrid()
        {
            grid.Clear();
            OnGridCleared();
            var filter = GameFilter;
            T first = null, selectedItem = null;
            VisualElement selected = null;
            foreach (var item in Items)
            {
                if (filter != null && item.SourceGame != filter) continue;
                var card = SpawnCard(item);
                if (card == null) break;
                if (first == null) first = item;
                if (IsSelected(item))
                {
                    card.AddToClassList("selected");
                    selected = card;
                    selectedItem = item;
                }
                OnCardSpawned(card, item);
            }
            UiAnim.Stagger(grid, cardStep, cardFirst, "pop");
            resting = selectedItem != null ? selectedItem : first;
            Show(resting);
            return selected ?? (grid.childCount > 0 ? grid[0] : null);
        }

        Button SpawnCard(T item)
        {
            var card = UiTree.Spawn<Button>(cardTemplate, grid);
            if (card == null) return null;
            card.Q("card-image")?.SetImage(CardImage(item));
            card.SetText("card-title", item.DisplayName);
            var tag = card.Q<Label>("card-tag");
            if (tag != null)
            {
                var text = CardTag(item);
                tag.text = text;
                tag.SetVisible(!string.IsNullOrEmpty(text));
            }
            card.clicked += () =>
            {
                Click();
                Pick(item);
            };
            card.RegisterCallback<FocusInEvent>(_ => OnCardFocused(item));
            card.RegisterCallback<PointerEnterEvent>(_ => OnCardHovered(item));
            card.RegisterCallback<PointerLeaveEvent>(_ => restore.ExecuteLater(hoverRestoreMs));
            return card;
        }
    }
}
