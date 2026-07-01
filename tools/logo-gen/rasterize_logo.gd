# Rasterize the brand logo SVG to a transparent PNG for the engine boot splash.
# Uses Image.load_svg_from_string (ThorVG, CPU-side) so it runs fully headless —
# no GPU/window needed, unlike a Viewport screenshot.
#
#   godot --headless --path client -s <abs>/tools/logo-gen/rasterize_logo.gd
extends SceneTree

const SRC := "res://assets/ui/logo.svg"
const OUT := "res://assets/ui/logo.png"
const SCALE := 0.8  # viewBox is 1000x1098 -> ~800x878 px on a dark boot screen

func _initialize() -> void:
	var svg := FileAccess.get_file_as_string(SRC)
	if svg.is_empty():
		push_error("could not read %s" % SRC)
		quit(1)
		return
	var img := Image.new()
	var err := img.load_svg_from_string(svg, SCALE)
	if err != OK:
		push_error("load_svg_from_string failed: %d" % err)
		quit(1)
		return
	err = img.save_png(OUT)
	if err != OK:
		push_error("save_png failed: %d" % err)
		quit(1)
		return
	print("LOGO_PNG_SAVED:%s  %dx%d" % [ProjectSettings.globalize_path(OUT), img.get_width(), img.get_height()])
	quit()
