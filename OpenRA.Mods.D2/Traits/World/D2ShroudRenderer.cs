#region Copyright & License Information
/*
 * Copyright 2007-2016 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class D2ShroudRendererInfo : ITraitInfo
	{
		public readonly string Sequence = "shroud";
		[SequenceReference("Sequence")] public readonly string[] ShroudVariants = new[] { "shroud" };
		[SequenceReference("Sequence")] public readonly string[] FogVariants = new[] { "fog" };

		[PaletteReference] public readonly string ShroudPalette = "shroud";
		[PaletteReference] public readonly string FogPalette = "fog";

		[Desc("Bitfield of shroud directions for each frame. Lower four bits are",
			"corners clockwise from TL; upper four are edges clockwise from top")]
		public readonly int[] Index = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

		[Desc("Hide or not map borders under shroud")]
		public readonly bool ShroudOnMapBorders = false;

		[Desc("Override for source art that doesn't define a fully shrouded tile")]
		[SequenceReference("Sequence")] public readonly string OverrideFullShroud = null;
		public readonly int OverrideShroudIndex = 15;

		[Desc("Override for source art that doesn't define a fully fogged tile")]
		[SequenceReference("Sequence")] public readonly string OverrideFullFog = null;
		public readonly int OverrideFogIndex = 15;

		public readonly BlendMode ShroudBlend = BlendMode.Alpha;
		public object Create(ActorInitializer init) { return new D2ShroudRenderer(init.World, this); }
	}

	public sealed class D2ShroudRenderer : IRenderShroud, IWorldLoaded, INotifyActorDisposing
	{
		[Flags]
		enum Edges : byte
		{
			None = 0,
			Top = 0x1,
			Right = 0x2,
			Bottom = 0x4,
			Left = 0x8,
			All = Top | Right | Bottom | Left,
			TopLeft = Top | Left,
			TopRight = Top | Right,
			BottomLeft = Bottom | Left,
			BottomRight = Bottom | Right
		}

		struct TileInfo
		{
			public readonly float3 ScreenPosition;
			public readonly byte Variant;

			public TileInfo(float3 screenPosition, byte variant)
			{
				ScreenPosition = screenPosition;
				Variant = variant;
			}
		}

		readonly D2ShroudRendererInfo info;
		readonly Map map;
		readonly Edges notVisibleEdges;
		readonly byte variantStride;
		readonly byte[] edgesToSpriteIndexOffset;

		readonly CellLayer<TileInfo> tileInfos;
		readonly Sprite[] fogSprites, shroudSprites;
		readonly HashSet<PPos> cellsDirty = new HashSet<PPos>();
		readonly HashSet<PPos> cellsAndNeighborsDirty = new HashSet<PPos>();

		Shroud currentShroud;
		Func<PPos, bool> visibleUnderShroud, visibleUnderFog;
		TerrainSpriteLayer shroudLayer, fogLayer;

		public D2ShroudRenderer(World world, D2ShroudRendererInfo info)
		{
			if (info.ShroudVariants.Length != info.FogVariants.Length)
				throw new ArgumentException("ShroudRenderer must define the same number of shroud and fog variants!", "info");

			if ((info.OverrideFullFog == null) ^ (info.OverrideFullShroud == null))
				throw new ArgumentException("ShroudRenderer cannot define overrides for only one of shroud or fog!", "info");

			if (info.ShroudVariants.Length > byte.MaxValue)
				throw new ArgumentException("ShroudRenderer cannot define this many shroud and fog variants.", "info");

			if (info.Index.Length >= byte.MaxValue)
				throw new ArgumentException("ShroudRenderer cannot define this many indexes for shroud directions.", "info");

			this.info = info;
			map = world.Map;

			tileInfos = new CellLayer<TileInfo>(map);

			// Load sprite variants
			var variantCount = info.ShroudVariants.Length;
			variantStride = (byte)(info.Index.Length + (info.OverrideFullShroud != null ? 1 : 0));
			shroudSprites = new Sprite[variantCount * variantStride];
			fogSprites = new Sprite[variantCount * variantStride];

			var sequenceProvider = map.Rules.Sequences;
			for (var j = 0; j < variantCount; j++)
			{
				var shroud = sequenceProvider.GetSequence(info.Sequence, info.ShroudVariants[j]);
				var fog = sequenceProvider.GetSequence(info.Sequence, info.FogVariants[j]);
				for (var i = 0; i < info.Index.Length; i++)
				{
					shroudSprites[j * variantStride + i] = shroud.GetSprite(i);
					fogSprites[j * variantStride + i] = fog.GetSprite(i);
				}

				if (info.OverrideFullShroud != null)
				{
					var i = (j + 1) * variantStride - 1;
					shroudSprites[i] = sequenceProvider.GetSequence(info.Sequence, info.OverrideFullShroud).GetSprite(0);
					fogSprites[i] = sequenceProvider.GetSequence(info.Sequence, info.OverrideFullFog).GetSprite(0);
				}
			}

			// Mapping of shrouded directions -> sprite index
			edgesToSpriteIndexOffset = new byte[(byte)(Edges.All) + 1];
			for (var i = 0; i < info.Index.Length; i++)
				edgesToSpriteIndexOffset[info.Index[i]] = (byte)i;

			if (info.OverrideFullShroud != null)
				edgesToSpriteIndexOffset[info.OverrideShroudIndex] = (byte)(variantStride - 1);

			notVisibleEdges = Edges.All;
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			// Initialize tile cache
			// This includes the region outside the visible area to cover any sprites peeking outside the map
			foreach (var uv in w.Map.AllCells.MapCoords)
			{
				var pos = w.Map.CenterOfCell(uv.ToCPos(map));
				var screen = wr.Screen3DPosition(pos - new WVec(0, 0, pos.Z));
				var variant = (byte)Game.CosmeticRandom.Next(info.ShroudVariants.Length);
				tileInfos[uv] = new TileInfo(screen, variant);
			}

			// Dirty the whole projected space
			DirtyCells(map.AllCells.MapCoords.Select(uv => (PPos)uv));

			// All tiles are visible in the editor
			if (w.Type == WorldType.Editor)
				visibleUnderShroud = _ => true;
			else
				visibleUnderShroud = puv => map.Contains(puv);

			visibleUnderFog = puv => map.Contains(puv);

			var shroudSheet = shroudSprites[0].Sheet;
			if (shroudSprites.Any(s => s.Sheet != shroudSheet))
				throw new InvalidDataException("Shroud sprites span multiple sheets. Try loading their sequences earlier.");

			var shroudBlend = shroudSprites[0].BlendMode;
			if (shroudSprites.Any(s => s.BlendMode != shroudBlend))
				throw new InvalidDataException("Shroud sprites must all use the same blend mode.");

			var fogSheet = fogSprites[0].Sheet;
			if (fogSprites.Any(s => s.Sheet != fogSheet))
				throw new InvalidDataException("Fog sprites span multiple sheets. Try loading their sequences earlier.");

			var fogBlend = fogSprites[0].BlendMode;
			if (fogSprites.Any(s => s.BlendMode != fogBlend))
				throw new InvalidDataException("Fog sprites must all use the same blend mode.");

			shroudLayer = new TerrainSpriteLayer(w, wr, shroudSheet, shroudBlend, wr.Palette(info.ShroudPalette), false);
			fogLayer = new TerrainSpriteLayer(w, wr, fogSheet, fogBlend, wr.Palette(info.FogPalette), false);
		}

		Edges GetEdges(PPos puv, Func<PPos, bool> isVisible)
		{
			if (!isVisible(puv))
				return notVisibleEdges;

			var cell = ((MPos)puv).ToCPos(map);

			var edge = Edges.None;
			if (!isVisible((PPos)(cell + new CVec(0, -1)).ToMPos(map))) edge |= Edges.Top;
			if (!isVisible((PPos)(cell + new CVec(1, 0)).ToMPos(map))) edge |= Edges.Right;
			if (!isVisible((PPos)(cell + new CVec(0, 1)).ToMPos(map))) edge |= Edges.Bottom;
			if (!isVisible((PPos)(cell + new CVec(-1, 0)).ToMPos(map))) edge |= Edges.Left;

			if (!info.ShroudOnMapBorders)
			{
				var mpos = cell.ToMPos(map);
				if ( edge == Edges.Top && mpos.V <= 1 ) edge = Edges.None;
				if ( edge == Edges.Left && mpos.U <= 1 ) edge = Edges.None;
				if ( edge == Edges.Bottom && mpos.V >= map.Tiles.Size.Height - 2 ) edge = Edges.None;
				if ( edge == Edges.Right && mpos.U >= map.Tiles.Size.Width - 2 ) edge = Edges.None;

				if ( edge == Edges.TopLeft && mpos.V <= 1 && mpos.U <= 1) edge = Edges.None;
				if ( edge == Edges.TopRight && mpos.V <= 1 && mpos.U >= map.Tiles.Size.Width - 2) edge = Edges.None;
				if ( edge == Edges.BottomLeft && mpos.V >= map.Tiles.Size.Height - 2 && mpos.U <= 1) edge = Edges.None;
				if ( edge == Edges.BottomRight && mpos.V >= map.Tiles.Size.Height - 2 && mpos.U >= map.Tiles.Size.Width - 2) edge = Edges.None;
			}

			return edge;
		}

		void DirtyCells(IEnumerable<PPos> cells)
		{
			// PERF: Many cells in the shroud change every tick. We only track the changes here and defer the real work
			// we need to do until we render. This allows us to avoid wasted work.
			cellsDirty.UnionWith(cells);
		}

		public void RenderShroud(Shroud shroud, WorldRenderer wr)
		{
			if (currentShroud != shroud)
			{
				if (currentShroud != null)
					currentShroud.CellsChanged -= DirtyCells;

				if (shroud != null)
					shroud.CellsChanged += DirtyCells;

				// Needs the anonymous function to ensure the correct overload is chosen
				if (shroud != null)
					visibleUnderShroud = puv => currentShroud.IsExplored(puv);
				else
					visibleUnderShroud = puv => map.Contains(puv);

				if (shroud != null)
					visibleUnderFog = puv => currentShroud.IsVisible(puv);
				else
					visibleUnderFog = puv => map.Contains(puv);

				currentShroud = shroud;
				DirtyCells(map.ProjectedCellBounds);
			}

			// We need to update newly dirtied areas of the shroud.
			// Expand the dirty area to cover the neighboring cells, since shroud is affected by neighboring cells.
			foreach (var uv in cellsDirty)
			{
				cellsAndNeighborsDirty.Add(uv);
				var cell = ((MPos)uv).ToCPos(map);
				foreach (var direction in CVec.Directions)
					cellsAndNeighborsDirty.Add((PPos)(cell + direction).ToMPos(map));
			}

			foreach (var puv in cellsAndNeighborsDirty)
			{
				var uv = (MPos)puv;
				if (!tileInfos.Contains(uv))
					continue;

				var tileInfo = tileInfos[uv];
				var shroudSprite = GetSprite(shroudSprites, GetEdges(puv, visibleUnderShroud), tileInfo.Variant);
				var shroudPos = tileInfo.ScreenPosition;
				if (shroudSprite != null)
					shroudPos += shroudSprite.Offset - 0.5f * shroudSprite.Size;

				var fogSprite = GetSprite(fogSprites, GetEdges(puv, visibleUnderFog), tileInfo.Variant);
				var fogPos = tileInfo.ScreenPosition;
				if (fogSprite != null)
					fogPos += fogSprite.Offset - 0.5f * fogSprite.Size;

				shroudLayer.Update(uv, shroudSprite, shroudPos);
				fogLayer.Update(uv, fogSprite, fogPos);
			}

			cellsDirty.Clear();
			cellsAndNeighborsDirty.Clear();

			fogLayer.Draw(wr.Viewport);
			shroudLayer.Draw(wr.Viewport);
		}

		Sprite GetSprite(Sprite[] sprites, Edges edges, int variant)
		{
			if (edges == Edges.None)
				return null;

			return sprites[variant * variantStride + edgesToSpriteIndexOffset[(byte)edges]];
		}

		bool disposed;
		public void Disposing(Actor self)
		{
			if (disposed)
				return;

			shroudLayer.Dispose();
			fogLayer.Dispose();
			disposed = true;
		}
	}
}
