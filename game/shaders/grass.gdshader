// GPU grass field around the camera focus (look of the first prototype, RimworldTest: 3-segment blades, dark roots,
// tips varying between two greens with dry patches, normals bent up so whole clumps shade evenly, flowers).
// Instances are fixed slots on whole cells of a window that follows the focus; the node snaps to whole cells
// (pairs of cells for the flower field). Every random property is hashed from the WORLD cell, so a clump keeps its
// exact place, size and turn when the window moves (no re-shuffling, no shimmer while panning).
shader_type spatial;
render_mode world_vertex_coords, cull_disabled, diffuse_lambert_wrap, specular_disabled;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D height_map : filter_linear, repeat_disable; // r = ground height (m), (W+1)x(H+1)
uniform float radius = 40.0;
uniform float density_scale = 1.0;
uniform int kind = 0;          // 0 grass, 1 yellow/white flowers, 2 purple/blue flowers
uniform float slot_cells = 1.0; // cells per slot side (2 for the flower fields)
uniform float slots_per_cell = 3.0;
uniform vec3 base_col : source_color = vec3(0.12, 0.26, 0.05);
uniform vec3 tip_col : source_color = vec3(0.52, 0.74, 0.18);
uniform vec3 tip_col2 : source_color = vec3(0.36, 0.62, 0.12);
uniform vec3 dry_col : source_color = vec3(0.70, 0.62, 0.30);

varying float vh;
varying vec3 vcol;
varying vec2 vorigin;
varying float vflower;

void vertex() {
	vec3 inst = MODEL_MATRIX[3].xyz;
	vec3 local = VERTEX - inst;
	// world slot (integer cell, or the lower corner of a 2x2 block) and the slot index within it
	vec2 slot = floor(inst.xz / slot_cells + 0.5) * slot_cells;
	float j = INSTANCE_CUSTOM.r * 8.0;
	vec2 off = vec2(hash12(slot * 1.13 + j * 17.31 + 3.7), hash12(slot * 0.91 + j * 5.17 + 11.3)) * slot_cells;
	vec2 origin = slot + off;
	vorigin = origin;
	float h0 = hash12(origin * 3.71 + 0.5);
	float h1 = hash12(origin * 2.33 + 7.1);

	vec4 cd = texelFetch(map_data, ivec2(clamp(floor(origin), vec2(0.0), map_size - 1.0)), 0);
	float dens = cd.g;
	float inside = step(0.0, origin.x) * step(0.0, origin.y) * step(origin.x, map_size.x) * step(origin.y, map_size.y);
	float keep;
	if (kind == 0) {
		keep = step(j + h0, dens * 4.0 * density_scale); // about dens × 4 clumps per cell (capped by the slots)
	} else {
		// flower meadows: patches of the low-frequency noise (yellow where high, purple where low), a few strays
		float fn = texture(noise_tex, origin * 0.012).a;
		float zone = kind == 1 ? smoothstep(0.58, 0.66, fn) : smoothstep(0.40, 0.32, fn);
		keep = step(h0, zone * 0.8 + 0.04) * step(0.25, dens);
	}
	float d = distance(origin, focus_pos.xz);
	float shrink = 1.0 - smoothstep(radius * 0.72, radius, d);
	float s = keep * inside * shrink * (0.65 + 0.7 * h1) * (0.55 + 0.5 * dens) * (1.0 - snow_cover * 0.55);

	float ang = h0 * 6.2831 + j;
	mat2 rot = mat2(vec2(cos(ang), sin(ang)), vec2(-sin(ang), cos(ang)));
	vec3 v = local;
	v.xz = rot * v.xz;
	v *= s;
	vh = UV.y;

	// wind: gusts from the noise texture plus a flutter; a gentle trample around the focused colonist
	float gust = texture(noise_tex, origin * 0.018 - wind_dir * TIME * 0.05).a;
	float flutter = sin(TIME * 2.7 + origin.x * 1.7 + origin.y * 1.1);
	vec2 sway = wind_dir * ((gust - 0.35) * 1.8 + 0.25) * wind_strength * 0.32;
	sway += vec2(flutter, -flutter * 0.6) * 0.035 * (0.3 + wind_strength);
	vec2 away = origin - reveal_pos.xz;
	sway += normalize(away + 1e-4) * smoothstep(1.2, 0.0, length(away)) * step(0.0, reveal_pos.w) * 0.35;
	float k = vh * vh;
	v.xz += sway * k * s;
	v.y -= length(sway) * k * 0.35 * s;

	float ground = texture(height_map, (origin + 0.5) / (map_size + 1.0)).r;
	VERTEX = vec3(origin.x, ground - 0.02, origin.y) + v;

	// colour: dark base, tips between two greens, dry patches (noise + season), per-blade shade in COLOR.r
	vec4 n = texture(noise_tex, origin * 0.028);
	vec4 n2 = texture(noise_tex, origin * 0.11);
	float s4 = mod(season, 4.0);
	float season_dry = smoothstep(1.4, 2.0, s4) * (1.0 - smoothstep(3.4, 3.9, s4)) * 0.7;
	float dry = smoothstep(0.66, 0.86, n.r) * 0.35 + season_dry;
	vec3 tip = mix(tip_col, tip_col2, n2.g);
	tip = mix(tip, dry_col, clamp(dry, 0.0, 1.0)) * (0.82 + 0.36 * h1);
	vcol = mix(base_col, tip, smoothstep(0.0, 1.0, vh));
	vflower = COLOR.a;
	if (COLOR.a > 0.5) vcol = pow(COLOR.rgb, vec3(2.2));
	else vcol *= COLOR.r;
	vcol = mix(vcol, vec3(0.6, 0.64, 0.71), snow_cover * (0.35 + 0.55 * vh));
	vec3 nn = NORMAL;
	nn.xz = rot * nn.xz;
	NORMAL = normalize(mix(nn, vec3(0.0, 1.0, 0.0), 0.82));
}

void fragment() {
	// two-sided cards: undo Godot's automatic back-face normal flip (the normal points up for both sides)
	if (!FRONT_FACING) NORMAL = -NORMAL;
	vec3 c = vcol * mix(0.55, 1.0, vh * vh);
	c *= cloud_shadow(vorigin) * 0.5 + 0.5;
	c *= mix(1.0, 0.75, wetness);
	ALBEDO = c;
	ROUGHNESS = mix(0.65, 0.35, wetness);
	BACKLIGHT = c * 0.55 * (1.0 - vflower * 0.5);
}
