// Oak canopy leaf cards: alpha-scissored leaf clusters with spherized normals for soft volume, back-lit translucency,
// wind sway weighted by vertex colour (r = branch flexibility), seasonal colours, snow on top, and a dithered
// cut-out around the colonist under your control so they stay visible under the trees.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx, world_vertex_coords;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D leaves : source_color, filter_linear_mipmap, repeat_disable;
uniform float alpha_cut = 0.42;

varying vec3 wpos;
varying float variation;
varying float canopy_h;
varying float cloud;

void vertex() {
	// world_vertex_coords: VERTEX is already in world space
	vec3 origin = MODEL_MATRIX[3].xyz;
	variation = fract(origin.x * 0.731 + origin.z * 0.317);
	float h = max(VERTEX.y - origin.y, 0.0);
	canopy_h = h;
	vec2 sway = wind_sway(origin, h * 0.12, 1.0) * (0.35 + COLOR.r * 0.65);
	// leaf flutter
	sway += vec2(sin(TIME * 4.3 + VERTEX.x * 2.1 + VERTEX.y), cos(TIME * 3.7 + VERTEX.z * 1.9)) * 0.025 * wind_strength * COLOR.r;
	VERTEX.xz += sway;
	wpos = VERTEX;
	cloud = cloud_shadow(origin.xz);
}

void fragment() {
	vec4 tex = texture(leaves, UV);
	// two-sided cards share one (spherized) normal: undo Godot's automatic back-face normal flip
	if (!FRONT_FACING) NORMAL = -NORMAL;
	if (tex.a < alpha_cut) discard;
	// reveal: dither leaves away around the controlled pawn
	if (reveal_pos.w > 0.0) {
		float d = length(wpos.xz - reveal_pos.xz);
		float above = smoothstep(reveal_pos.y + 0.5, reveal_pos.y + 2.0, wpos.y);
		float fade = smoothstep(reveal_pos.w, reveal_pos.w * 0.45, d) * above;
		float dither = fract(dot(FRAGCOORD.xy, vec2(0.5, 0.25)) + hash12(floor(FRAGCOORD.xy)) * 0.5);
		if (fade > dither) discard;
	}
	// per-tree hue variation (yellower to bluer greens) and a brighter sunlit crown top
	// linear-space greens; the leaf texture is normalised by its own mean leaf colour (also linear)
	vec3 summer = mix(vec3(0.075, 0.15, 0.025), vec3(0.045, 0.13, 0.04), variation) * (0.9 + variation * 0.25);
	vec3 base = season_foliage(summer, variation);
	vec3 col = tex.rgb / vec3(0.040, 0.105, 0.0095) * base;
	col *= mix(0.8, 1.2, COLOR.r);
	float up = clamp(NORMAL.y, 0.0, 1.0);
	col = mix(col, vec3(0.9, 0.92, 0.95), snow_cover * smoothstep(0.3, 0.8, (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y));
	col *= cloud;
	ALBEDO = col;
	ROUGHNESS = 0.75;
	SPECULAR = 0.25;
	BACKLIGHT = col * 0.75;
	AO = mix(0.6, 1.0, COLOR.r);
	AO_LIGHT_AFFECT = 0.4;
}
