// GPU grass field around the camera focus. Instances are a fixed pattern of clumps covering a square window; the
// window origin snaps to whole cells. Each clump looks up the map (density, ground height) at its world position,
// gets a per-cell random rotation/scale, shrinks away with distance and where the map has no grass, and sways in
// the wind. The CPU never touches individual blades.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_disabled, shadows_disabled, world_vertex_coords;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D height_map : filter_linear, repeat_disable; // r = ground height (m), (W+1)x(H+1)
uniform float radius = 45.0;
uniform float density_scale = 1.0;

varying float tip;
varying float shade;
varying vec3 wpos;

void vertex() {
	// world_vertex_coords: VERTEX is in world space; the node sits at the snapped window origin and every
	// instance is a pure translation, so the clump origin is MODEL_MATRIX[3] and the blade offset VERTEX - origin
	vec3 inst = MODEL_MATRIX[3].xyz;
	vec3 local = VERTEX - inst;
	vec2 world_xz = inst.xz;
	vec2 cell = floor(world_xz);
	float h = hash12(cell * 1.31 + fract(world_xz) * 7.7);
	float d = distance(world_xz, focus_pos.xz);
	vec2 uv = world_xz / map_size;
	float dens = texture(map_data, uv).g;
	float inside = step(0.0, world_xz.x) * step(0.0, world_xz.y) * step(world_xz.x, map_size.x) * step(world_xz.y, map_size.y);
	float keep = step(h, dens * 1.35 * density_scale) * inside;
	float scale = keep * (0.55 + 0.6 * dens) * (0.75 + h * 0.5) * smoothstep(radius, radius * 0.7, d);
	scale *= 1.0 - snow_cover * 0.75;
	float ang = h * 6.2831;
	mat2 rot = mat2(vec2(cos(ang), sin(ang)), vec2(-sin(ang), cos(ang)));
	vec3 v = local;
	v.xz = rot * v.xz;
	v *= scale;
	tip = clamp(local.y / 0.6, 0.0, 1.0);
	vec2 sway = wind_sway(vec3(world_xz.x, 0.0, world_xz.y), tip, 1.0) * 0.9;
	// gentle trample around the focus/colonist
	vec2 away = world_xz - reveal_pos.xz;
	float push = smoothstep(1.2, 0.0, length(away)) * step(0.0, reveal_pos.w);
	sway += normalize(away + 1e-4) * push * 0.5 * tip;
	v.xz += sway * scale;
	float ground = texture(height_map, (world_xz + 0.5) / (map_size + 1.0)).r;
	vec3 world = vec3(world_xz.x, ground, world_xz.y) + v;
	shade = h;
	wpos = world;
	VERTEX = world;
}

void fragment() {
	vec3 base = mix(vec3(0.16, 0.26, 0.07), vec3(0.30, 0.38, 0.12), shade);
	vec3 col = season_grass(base);
	col = mix(col * 0.45, col * 1.15, tip);   // darker at the root
	col *= 1.0 - wetness * 0.2;
	col *= cloud_shadow(wpos.xz);
	ALBEDO = col;
	ROUGHNESS = 0.8;
	BACKLIGHT = col * 0.5 * tip;
	AO = mix(0.4, 1.0, tip);
	AO_LIGHT_AFFECT = 0.6;
}
