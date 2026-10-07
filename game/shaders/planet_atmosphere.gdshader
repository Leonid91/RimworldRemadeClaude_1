// Atmospheric halo: back faces of a slightly larger sphere, additive. Brightness peaks at the limb and on the
// sunlit side, with a warm band along the terminator.
shader_type spatial;
render_mode blend_add, depth_draw_never, cull_front, unshaded;

uniform vec3 sun_dir = vec3(0.6, 0.3, 0.7);   // world space
uniform vec3 day_color : source_color = vec3(0.35, 0.62, 1.0);
uniform vec3 sunset_color : source_color = vec3(1.0, 0.55, 0.30);
uniform float intensity = 1.6;
uniform float planet_radius = 1.0;
uniform float atmo_radius = 1.07;

varying vec3 world_pos;
varying vec3 center_ws;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	center_ws = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
}

void fragment() {
	vec3 cam = (INV_VIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	vec3 rd = normalize(world_pos - cam);
	vec3 oc = cam - center_ws;
	float scale = length((MODEL_MATRIX * vec4(1.0, 0.0, 0.0, 0.0)).xyz);
	float R = atmo_radius * scale, r = planet_radius * scale;
	// closest approach of the view ray to the planet centre
	float tca = -dot(oc, rd);
	float d2 = dot(oc, oc) - tca * tca;
	float d = sqrt(max(d2, 0.0));
	// optical path through the shell, normalised
	float h = clamp((R - d) / (R - r), 0.0, 1.0);
	float glow = pow(h, 2.2);
	if (d < r) glow *= 0.35; // in front of the planet disk: thinner
	vec3 p = cam + rd * tca - center_ws;
	vec3 n = normalize(p);
	float sun = dot(n, normalize(sun_dir));
	float lit = smoothstep(-0.35, 0.45, sun);
	float term = exp(-pow(sun * 3.0, 2.0));
	vec3 col = mix(day_color, sunset_color, term * 0.8);
	ALBEDO = col * glow * lit * intensity;
}
