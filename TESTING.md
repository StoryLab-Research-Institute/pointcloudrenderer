# Testing the Octree Renderer

These are the manual Unity steps required to test each phase. They cannot be automated from code.

---

## Prerequisites

- Unity project using URP.
- This package installed (branch `260512-octree`).

---

## Phase 1 — Test Harness (shader + render feature validation)

### 1. Add the Render Feature

1. Open **Project Settings → Graphics**.
2. Select your active **URP Renderer** asset (e.g. `UniversalRenderPipelineAsset_Renderer`).
3. In the Inspector, click **Add Renderer Feature** and choose **Point Cloud Render Feature**.

### 2. Create the Octree material

1. In the Project window, locate `DefaultPointCloud.mat` (under `Runtime/Shaders/URP/`).
2. Duplicate it (`Ctrl+D`) and rename it `DefaultOctreePointCloud.mat`.
3. In the Inspector for the new material, change the shader to **StoryLab PointCloud/URP Octree**.
4. Adjust Point Size, Color Mode, and Point Shape as desired.

### 3. Add SimplePointCloudRenderer to an imported mesh

1. Import a `.ply` file using the existing importer (old pipeline — leave it untouched).
2. In the scene, select the imported point cloud GameObject (or one of its LOD children).
3. **Disable** the existing `MeshRenderer` component (uncheck it in the Inspector).
4. Click **Add Component** and add **Simple Point Cloud Renderer**.
5. Assign `DefaultOctreePointCloud.mat` to the **Material** field.
   - The **Source** field auto-populates from the `MeshFilter` on the same GameObject.
6. Enter Play Mode (or keep in Edit Mode if using Execute Always).

**Expected result:** The point cloud renders using the new shader. Points appear as squares/diamonds/circles depending on the material setting.

**Note:** `SimplePointCloudRenderer` bakes world-space positions at `OnEnable`. Moving the GameObject after enabling will not move the rendered points. This is intentional for this test component.

---

## Phase 3 — Octree Renderer (full pipeline)

### 1. Build the Octree Asset

1. In the Project window, select the **Mesh** asset produced by the PLY importer (not the prefab — the actual `Mesh` sub-asset).
2. In the menu bar, choose **Assets → StoryLab PointCloud → Build Octree from Mesh**.
3. A save dialog appears. Choose a location and name (e.g. `MyCloud_Octree.asset`).
4. Wait for the progress bar. Large clouds (60M points) will take several minutes.

### 2. Add OctreeRenderer to the scene

1. Create an empty GameObject (or reuse the imported prefab root).
2. Add **Octree Renderer** component.
3. Assign:
   - **Asset**: the `OctreeAsset` built in the previous step.
   - **Material**: `DefaultOctreePointCloud.mat` (same material as Phase 1).
4. Disable or remove the `SimplePointCloudRenderer` and `MeshRenderer` components if present.
5. Enter Play Mode.

**Expected result:** The octree renders at the configured `PointBudget` (default 2M points). Closer nodes show more detail; distant nodes switch to coarser representations. Adjust `ScreenErrorThreshold` to trade sharpness against point budget.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Nothing renders | Render Feature not added to the URP Renderer, or no drawables registered. |
| Shader compile error | Ensure the project targets Shader Model 4.5+ (URP default is fine). |
| Points at origin | `_source` MeshFilter is null or mesh has no vertices. |
| Wrong colours | Check the mesh has `colors32` data; if not, renderer defaults to white. |
| Build fails with null reference | Select a **Mesh** sub-asset (not the prefab) before running Build Octree. |
