// Deer vertex animation (one MultiMesh for all deer of a kind). UV2.x = bone id. INSTANCE_CUSTOM:
// x = gait phase (cycles), y = state (0 idle, 1 walk, 2 run, 3 graze, 4 rest, 5 dead), w = coat variation.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

varying vec3 wpos;
varying float coat;

mat3 rot_x(float a) { float c = cos(a), s = sin(a); return mat3(vec3(1, 0, 0), vec3(0, c, s), vec3(0, -s, c)); }

void vertex() {
	int bone = int(UV2.x + 0.5);
	float phase = INSTANCE_CUSTOM.x * 6.28318;
	int state = int(INSTANCE_CUSTOM.y + 0.5);
	float seed = INSTANCE_CUSTOM.w * 37.0;
	vec3 v = VERTEX;
	vec3 n = NORMAL;
	float leg_amp = state == 1 ? 0.42 : state == 2 ? 0.95 : 0.0;
	vec3 hips[4] = vec3[](vec3(-0.11, 0.86, -0.42), vec3(0.11, 0.86, -0.42), vec3(-0.12, 0.92, 0.42), vec3(0.12, 0.92, 0.42));
	if (bone >= 2 && bone <= 5) {
		int leg = bone - 2;
		float off = state == 2 ? (leg < 2 ? 0.0 : 3.14159) : ((leg == 0 || leg == 3) ? 0.0 : 3.14159);
		float a = sin(phase + off + (state == 2 && (leg == 1 || leg == 3) ? 0.35 : 0.0)) * leg_amp;
		if (state == 4) a = leg < 2 ? -1.45 : 1.5;   // folded under the body
		if (state == 5) a = leg < 2 ? 0.5 : -0.4;   // stiff
		mat3 r = rot_x(a);
		v = hips[leg] + r * (v - hips[leg]);
		n = r * n;
	}
	if (bone == 1) {
		vec3 piv = vec3(0.0, 1.05, -0.52);
		float a = 0.0;
		if (state == 3) a = -1.05 + sin(TIME * 2.3 + seed) * 0.05;          // grazing
		else if (state == 0 || state == 4) a = sin(TIME * 0.7 + seed) * 0.08;  // looking around
		else if (state == 2) a = 0.25 + sin(phase * 2.0) * 0.08;
		else a = sin(phase * 2.0) * 0.05;
		mat3 r = rot_x(a);
		v = piv + r * (v - piv);
		n = r * n;
	}
	if (bone == 6) {
		vec3 piv = vec3(0.0, 1.12, 0.6);
		float a = state == 2 ? 0.9 : sin(TIME * 3.0 + seed) * 0.12; // white flag raised when fleeing
		mat3 r = rot_x(a);
		v = piv + r * (v - piv);
	}
	// body motion
	if (state == 1) v.y += abs(sin(phase)) * 0.025;
	if (state == 2) { v.y += abs(sin(phase)) * 0.09; v = rot_x(sin(phase) * 0.08) * (v - vec3(0, 1.0, 0)) + vec3(0, 1.0, 0); }
	if (state == 4) v.y -= bone >= 2 && bone <= 5 ? 0.42 : 0.5;
	VERTEX = v;
	NORMAL = n;
	wpos = (MODEL_MATRIX * vec4(v, 1.0)).xyz;
	coat = INSTANCE_CUSTOM.w;
}

void fragment() {
	vec3 col = pow(COLOR.rgb, vec3(2.2)) * (0.9 + coat * 0.2); // vertex colours are sRGB
	col *= cloud_shadow(wpos.xz);
	col = mix(col, col * 0.75, wetness);
	ALBEDO = col;
	ROUGHNESS = 0.85;
	SPECULAR = 0.25;
}
