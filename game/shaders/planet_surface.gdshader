// Planet surface: smooth sphere shaded from baked cubemaps (albedo with rivers, object-space normals + elevation)
// and an optional data overlay (temperature / precipitation from the climate simulation, or elevation) coloured
// per hexagon: a tile-id cubemap tells which tile is under each pixel and a small per-tile texture holds the values,
// so the map reads like the biome map (one colour per region, thin outlines) and seas are shown in grey.
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform samplerCube albedo_map : source_color, filter_linear_mipmap, repeat_disable;
uniform samplerCube normal_map : filter_linear_mipmap, repeat_disable;   // rgb = object normal * 0.5 + 0.5, a = elevation 0..1 (0.5 = sea level)
uniform samplerCube tile_ids : filter_nearest, repeat_disable;          // rgb = 24-bit tile index
uniform sampler2D tile_data : filter_nearest, repeat_disable;           // per tile: r = overlay value 0..1, g = water
uniform int data_width = 512;
uniform float tile_angle = 0.017;                                       // radians between tile centres
uniform int overlay_mode = 0;      // 0 none, 1 temperature, 2 elevation, 3 precipitation
uniform float overlay_strength = 1.0;
uniform float time_scroll = 0.0;
uniform bool map_style = false; // flat map look (landing site, in-game planet): matte water, no sun glare
uniform int debug_mode = 0; // 1: agreement of baked normal with the geometric normal (white = correct)

varying vec3 obj_pos;

vec3 ramp_temperature(float t) {
	// -45°C .. +45°C
	vec3 c0 = vec3(0.30, 0.10, 0.55);
	vec3 c1 = vec3(0.15, 0.35, 0.85);
	vec3 c2 = vec3(0.55, 0.85, 0.95);
	vec3 c3 = vec3(0.45, 0.80, 0.35);
	vec3 c4 = vec3(0.98, 0.85, 0.25);
	vec3 c5 = vec3(0.95, 0.45, 0.15);
	vec3 c6 = vec3(0.75, 0.10, 0.10);
	float x = clamp(t, 0.0, 1.0) * 6.0;
	if (x < 1.0) return mix(c0, c1, x);
	if (x < 2.0) return mix(c1, c2, x - 1.0);
	if (x < 3.0) return mix(c2, c3, x - 2.0);
	if (x < 4.0) return mix(c3, c4, x - 3.0);
	if (x < 5.0) return mix(c4, c5, x - 4.0);
	return mix(c5, c6, x - 5.0);
}

vec3 ramp_precip(float t) {
	vec3 c0 = vec3(0.78, 0.66, 0.45);
	vec3 c1 = vec3(0.70, 0.78, 0.40);
	vec3 c2 = vec3(0.25, 0.65, 0.40);
	vec3 c3 = vec3(0.15, 0.50, 0.80);
	vec3 c4 = vec3(0.25, 0.20, 0.70);
	float x = clamp(t, 0.0, 1.0) * 4.0;
	if (x < 1.0) return mix(c0, c1, x);
	if (x < 2.0) return mix(c1, c2, x - 1.0);
	if (x < 3.0) return mix(c2, c3, x - 2.0);
	return mix(c3, c4, x - 3.0);
}

vec3 ramp_elevation(float e) {
	// e: 0..1 with 0.5 = sea level
	if (e < 0.5) {
		float d = (0.5 - e) * 2.0;
		return mix(vec3(0.35, 0.65, 0.85), vec3(0.03, 0.08, 0.30), sqrt(d));
	}
	float h = (e - 0.5) * 2.0;
	vec3 c0 = vec3(0.20, 0.55, 0.25);
	vec3 c1 = vec3(0.75, 0.75, 0.35);
	vec3 c2 = vec3(0.55, 0.35, 0.20);
	vec3 c3 = vec3(0.95, 0.95, 0.97);
	float x = clamp(h * 2.2, 0.0, 1.0) * 3.0;
	if (x < 1.0) return mix(c0, c1, x);
	if (x < 2.0) return mix(c1, c2, x - 1.0);
	return mix(c2, c3, x - 2.0);
}

void vertex() {
	obj_pos = VERTEX;
}

int tile_at(vec3 d) {
	vec3 c = texture(tile_ids, d).rgb;
	return int(c.r * 255.0 + 0.5) + int(c.g * 255.0 + 0.5) * 256 + int(c.b * 255.0 + 0.5) * 65536;
}

void fragment() {
	vec3 dir = normalize(obj_pos);
	vec3 base = texture(albedo_map, dir).rgb;
	vec4 nm = texture(normal_map, dir);
	vec3 n_obj = normalize(nm.rgb * 2.0 - 1.0);
	float elev = nm.a;
	bool water = elev < 0.5;

	vec3 col = base;
	if (overlay_mode != 0) {
		int id = tile_at(dir);
		vec2 dv = texelFetch(tile_data, ivec2(id % data_width, id / data_width), 0).rg;
		vec3 oc = overlay_mode == 1 ? ramp_temperature(dv.r) : overlay_mode == 2 ? ramp_elevation(dv.r) : ramp_precip(dv.r);
		if (dv.g > 0.5) {
			// seas and lakes in grey (brighter = higher value) so the land stands out
			float g = dot(oc, vec3(0.3, 0.55, 0.15));
			oc = vec3(0.16 + 0.34 * g);
		}
		// thin hexagon outlines once a tile spans enough pixels to read them
		vec3 ddx = dFdx(dir), ddy = dFdy(dir);
		float px_per_tile = tile_angle / max(length(ddx), 1e-6);
		if (px_per_tile > 7.0) {
			bool edge = tile_at(normalize(dir + ddx)) != id || tile_at(normalize(dir + ddy)) != id;
			if (edge) oc *= mix(1.0, 0.72, smoothstep(7.0, 14.0, px_per_tile));
		}
		col = mix(col, pow(oc, vec3(2.2)), overlay_strength); // ramps are authored in sRGB, like the UI legends
	}
	if (debug_mode == 1) {
		float agree = dot(n_obj, dir);
		col = agree > 0.9 ? vec3(1.0) : agree > 0.0 ? vec3(1.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
		// face id tint
		vec3 a = abs(dir);
		col *= a.x > a.y && a.x > a.z ? vec3(1.0, 0.7, 0.7) : a.y > a.z ? vec3(0.7, 1.0, 0.7) : vec3(0.7, 0.7, 1.0);
	}
	ALBEDO = col;
	NORMAL = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(n_obj, 0.0)).xyz);
	ROUGHNESS = map_style ? 0.9 : (water && overlay_mode == 0 ? 0.28 : 0.85);
	SPECULAR = map_style ? 0.12 : (water ? 0.6 : 0.25);
	METALLIC = 0.0;
	// a hint of rim light on the night side so the globe never reads as a flat disk
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
	// heatmaps glow a little so their colours read evenly across the lit and shaded side
	EMISSION = vec3(0.05, 0.09, 0.16) * rim * 0.6 + col * (overlay_mode != 0 ? 0.3 : 0.015);
	if (debug_mode == 1) { EMISSION = col; ALBEDO = vec3(0.0); }
}
