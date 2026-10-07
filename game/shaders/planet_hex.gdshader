// Hex outlines on the globe (hovered tile, its neighbourhood, the selection). Vertex colour alpha fades the
// neighbourhood; the selection pulses.
shader_type spatial;
render_mode blend_add, depth_draw_never, cull_disabled, unshaded, shadows_disabled;

uniform float pulse = 0.0;

void fragment() {
	float p = 1.0 + 0.35 * sin(TIME * 4.0) * pulse;
	ALBEDO = COLOR.rgb * p;
	ALPHA = COLOR.a;
}
