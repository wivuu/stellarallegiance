extends Node
# Renders a framed thumbnail PNG for every .glb in a source directory.
#
# Runtime-loads each GLB via GLTFDocument (no res:// import needed, so it works
# on the gitignored, out-of-tree pick-assets folder), frames it with an iso
# camera, and captures a transparent-background PNG per model.
#
# Args (after `--`):
#   --src <dir>    absolute path to folder of .glb files (required)
#   --out <dir>    absolute path for output PNGs (required)
#   --size <px>    square thumbnail size (default 320)
#   --limit <n>    render only first n models (0 = all, default 0)

const DEFAULT_SIZE := 320

var src_dir := ""
var out_dir := ""
var thumb_size := DEFAULT_SIZE
var limit := 0

var viewport: SubViewport
var camera: Camera3D
var model_holder: Node3D

func _ready() -> void:
	_parse_args()
	if src_dir == "" or out_dir == "":
		push_error("Missing --src / --out")
		get_tree().quit(1)
		return

	DirAccess.make_dir_recursive_absolute(out_dir)
	_build_stage()

	var files := _list_glbs(src_dir)
	if limit > 0:
		files = files.slice(0, limit)

	print("[glb-gallery] rendering %d models @ %dpx -> %s" % [files.size(), thumb_size, out_dir])

	var idx := 0
	for f in files:
		idx += 1
		var name := f.get_file().get_basename()
		var ok := await _render_one(f)
		if ok:
			var img := viewport.get_texture().get_image()
			img.save_png(out_dir.path_join(name + ".png"))
			print("  [%d/%d] %s" % [idx, files.size(), name])
		else:
			print("  [%d/%d] %s  (FAILED to load)" % [idx, files.size(), name])

	print("[glb-gallery] done")
	get_tree().quit(0)

func _parse_args() -> void:
	var a := OS.get_cmdline_user_args()
	var i := 0
	while i < a.size():
		match a[i]:
			"--src": src_dir = a[i + 1]; i += 2
			"--out": out_dir = a[i + 1]; i += 2
			"--size": thumb_size = int(a[i + 1]); i += 2
			"--limit": limit = int(a[i + 1]); i += 2
			_: i += 1

func _list_glbs(dir: String) -> PackedStringArray:
	var out := PackedStringArray()
	var d := DirAccess.open(dir)
	if d == null:
		push_error("Cannot open src dir: " + dir)
		return out
	for f in d.get_files():
		if f.to_lower().ends_with(".glb"):
			out.append(dir.path_join(f))
	out.sort()
	return out

func _build_stage() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(thumb_size, thumb_size)
	viewport.transparent_bg = true
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	viewport.msaa_3d = Viewport.MSAA_4X
	add_child(viewport)

	var env := Environment.new()
	env.background_mode = Environment.BG_CLEAR_COLOR
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.55, 0.58, 0.65)
	env.ambient_light_energy = 1.0
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	var we := WorldEnvironment.new()
	we.environment = env
	viewport.add_child(we)

	var key := DirectionalLight3D.new()
	key.rotation_degrees = Vector3(-40, -50, 0)
	key.light_energy = 1.6
	viewport.add_child(key)

	var fill := DirectionalLight3D.new()
	fill.rotation_degrees = Vector3(-15, 130, 0)
	fill.light_energy = 0.5
	viewport.add_child(fill)

	camera = Camera3D.new()
	camera.fov = 35.0
	viewport.add_child(camera)

	model_holder = Node3D.new()
	viewport.add_child(model_holder)

func _render_one(path: String) -> bool:
	for c in model_holder.get_children():
		model_holder.remove_child(c)
		c.queue_free()

	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	var err := doc.append_from_file(path, state)
	if err != OK:
		return false
	var scene := doc.generate_scene(state)
	if scene == null:
		return false
	model_holder.add_child(scene)

	# Let transforms/meshes settle, then frame.
	await get_tree().process_frame
	var aabb := _scene_aabb(scene)
	if aabb.size.length() <= 0.0001:
		aabb = AABB(Vector3(-1, -1, -1), Vector3(2, 2, 2))
	_frame_camera(aabb)

	# A couple of frames so the render target is fully populated.
	await RenderingServer.frame_post_draw
	await get_tree().process_frame
	await RenderingServer.frame_post_draw
	return true

func _scene_aabb(node: Node) -> AABB:
	var acc := AABB()
	var have := false
	for m in _find_meshes(node):
		var mi := m as MeshInstance3D
		var local: AABB = mi.get_aabb()
		var gx: Transform3D = mi.global_transform
		# transform the 8 corners into world space
		for corner in _aabb_corners(local):
			var wp: Vector3 = gx * corner
			if not have:
				acc = AABB(wp, Vector3.ZERO)
				have = true
			else:
				acc = acc.expand(wp)
	return acc

func _find_meshes(node: Node) -> Array:
	var out := []
	if node is MeshInstance3D and node.mesh != null:
		out.append(node)
	for c in node.get_children():
		out.append_array(_find_meshes(c))
	return out

func _aabb_corners(a: AABB) -> Array:
	var p := a.position
	var s := a.size
	return [
		p,
		p + Vector3(s.x, 0, 0),
		p + Vector3(0, s.y, 0),
		p + Vector3(0, 0, s.z),
		p + Vector3(s.x, s.y, 0),
		p + Vector3(s.x, 0, s.z),
		p + Vector3(0, s.y, s.z),
		p + s,
	]

func _frame_camera(aabb: AABB) -> void:
	var center := aabb.position + aabb.size * 0.5
	var radius := aabb.size.length() * 0.5
	radius = max(radius, 0.001)
	var dir := Vector3(1.0, 0.7, 1.0).normalized()
	var fov_rad := deg_to_rad(camera.fov)
	var dist := (radius / sin(fov_rad * 0.5)) * 1.15
	camera.global_position = center + dir * dist
	camera.look_at(center, Vector3.UP)
	camera.near = max(dist - radius * 2.0, 0.01)
	camera.far = dist + radius * 3.0
