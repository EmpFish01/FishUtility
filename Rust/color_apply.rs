// ── Per-vertex color application ──
// The hot path: runs every frame, applies animated Oklab colors to mesh vertices.
// Parallelized with rayon.

use rayon::prelude::*;

/// Process one mesh group's vertices. All data for this group is contiguous.
///
/// This is the function called from C# for each mesh group on every frame.
///
/// # Safety
/// Pointers must be valid for `count` elements (`colors` for `count * 4` bytes).
pub unsafe fn apply_one_group(
    static_wrap_l: *const f32,
    static_wrap_a: *const f32,
    static_wrap_b: *const f32,
    vert_index: *const i32,
    normalized_pos: *const f32,
    config_idx: *const i32,
    config_wrap_starts: *const i32,
    config_smooth_lens: *const i32,
    config_masks: *const i32,
    config_shift_ints: *const i32,
    config_shift_fracs: *const f32,
    config_cycle_comps: *const f32,
    num_configs: usize,
    colors: *mut u8,
    count: usize,
) {
    if count == 0 || num_configs == 0 {
        return;
    }

    // Convert raw pointers to slices or integers BEFORE rayon (raw pointers are !Sync)
    let vert_idx = std::slice::from_raw_parts(vert_index, count);
    let norm_pos = std::slice::from_raw_parts(normalized_pos, count);
    let cfg_idx = std::slice::from_raw_parts(config_idx, count);
    let cfg_wrap_starts = std::slice::from_raw_parts(config_wrap_starts, num_configs);
    let cfg_smooth_lens = std::slice::from_raw_parts(config_smooth_lens, num_configs);
    let cfg_masks = std::slice::from_raw_parts(config_masks, num_configs);
    let cfg_shift_ints = std::slice::from_raw_parts(config_shift_ints, num_configs);
    let cfg_shift_fracs = std::slice::from_raw_parts(config_shift_fracs, num_configs);
    let cfg_cycle_comps = std::slice::from_raw_parts(config_cycle_comps, num_configs);

    // Raw pointers → usize for Sync (raw pointers are !Send+!Sync, integers are fine)
    let wrap_l_addr = static_wrap_l as usize;
    let wrap_a_addr = static_wrap_a as usize;
    let wrap_b_addr = static_wrap_b as usize;
    let colors_addr = colors as usize;

    // Process in parallel — random-access writes into the full mesh colors array
    (0..count).into_par_iter().for_each(|i| {
        let vi = vert_idx[i] as usize;
        let np = norm_pos[i];
        let cfg = cfg_idx[i] as usize;

            if cfg >= num_configs {
                let dst = (colors_addr as *mut u8).add(vi * 4);
                unsafe { *dst = 255; *dst.add(1) = 255; *dst.add(2) = 255; *dst.add(3) = 255; }
                return;
            }

            let wrap_base = cfg_wrap_starts[cfg] as usize;
            let smooth_len = cfg_smooth_lens[cfg] as usize;
            let mask = cfg_masks[cfg] as usize;
            let cycle_comp = cfg_cycle_comps[cfg];

            // Compute fractional position in the smooth LUT
            let np_comp = np * cycle_comp;
            let np_frac = np_comp - (np_comp as i32) as f32;
            let smooth_pos = np_frac * smooth_len as f32;
            let base_idx = smooth_pos as usize;
            let frac = smooth_pos - base_idx as f32;

            let shift_int = cfg_shift_ints[cfg] as usize;
            let shift_frac = cfg_shift_fracs[cfg];

            let mut idx = base_idx.wrapping_sub(shift_int) & mask;
            let mut f = frac - shift_frac;
            if f < 0.0 {
                f += 1.0;
                idx = idx.wrapping_sub(1) & mask;
            }
            let idx_next = (idx + 1) & mask;

            let one_minus_f = 1.0 - f;

            // SAFETY: wrap_l/a/b pointers were validated by caller. wrap_base+idx is within bounds.
            let wrap_l = wrap_l_addr as *const f32;
            let wrap_a = wrap_a_addr as *const f32;
            let wrap_b = wrap_b_addr as *const f32;
            let l_val = unsafe {
                *wrap_l.add(wrap_base + idx) * one_minus_f
                    + *wrap_l.add(wrap_base + idx_next) * f
            };
            let a_val = unsafe {
                *wrap_a.add(wrap_base + idx) * one_minus_f
                    + *wrap_a.add(wrap_base + idx_next) * f
            };
            let b_val = unsafe {
                *wrap_b.add(wrap_base + idx) * one_minus_f
                    + *wrap_b.add(wrap_base + idx_next) * f
            };

            let (r, g, b) = oklab_to_srgb_byte_fast(l_val, a_val, b_val);

            let dst = unsafe { (colors_addr as *mut u8).add(vi * 4) };
            unsafe { *dst = r; *dst.add(1) = g; *dst.add(2) = b; *dst.add(3) = 255; }
        });
}

#[inline(always)]
fn oklab_to_srgb_byte_fast(l: f32, a: f32, b_ok: f32) -> (u8, u8, u8) {
    // Oklab → LMS linear: result = L*1.0 + A*k1 + B*k2
    // mul_add chain is right-associative: a.mul_add(k1, b.mul_add(k2, base))
    let l_ = a.mul_add(0.3963377774, b_ok.mul_add(0.2158037573, l));
    let m_ = a.mul_add(-0.1055613458, b_ok.mul_add(-0.0638541728, l));
    let s_ = a.mul_add(-0.0894841775, b_ok.mul_add(-1.2914855480, l));

    // Cube
    let l_ = l_ * l_ * l_;
    let m_ = m_ * m_ * m_;
    let s_ = s_ * s_ * s_;

    // LMS → Linear sRGB
    let r_lin = l_.mul_add(4.0767416621, m_.mul_add(-3.3077363322, 0.2309101289 * s_));
    let g_lin = l_.mul_add(-1.2684380046, m_.mul_add(2.6097574011, -0.3413193761 * s_));
    let b_lin = l_.mul_add(-0.0041960863, m_.mul_add(-0.7034186147, 1.7076147010 * s_));

    // Clamp negative
    let r_lin = if r_lin < 0.0 { 0.0 } else { r_lin };
    let g_lin = if g_lin < 0.0 { 0.0 } else { g_lin };
    let b_lin = if b_lin < 0.0 { 0.0 } else { b_lin };

    // Highlight rolloff
    const K: f32 = 0.25;
    let r_lin = if r_lin > 1.0 { r_lin / (1.0 + (r_lin - 1.0) * K) } else { r_lin };
    let g_lin = if g_lin > 1.0 { g_lin / (1.0 + (g_lin - 1.0) * K) } else { g_lin };
    let b_lin = if b_lin > 1.0 { b_lin / (1.0 + (b_lin - 1.0) * K) } else { b_lin };

    // Linear → sRGB
    let r = linear_to_srgb(r_lin);
    let g = linear_to_srgb(g_lin);
    let b = linear_to_srgb(b_lin);

    let rb = (r * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
    let gb = (g * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
    let bb = (b * 255.0 + 0.5).clamp(0.0, 255.0) as u8;

    (rb, gb, bb)
}

#[inline(always)]
fn linear_to_srgb(linear: f32) -> f32 {
    if linear <= 0.0031308 {
        linear * 12.92
    } else {
        1.055 * linear.powf(1.0 / 2.4) - 0.055
    }
}

// ── Batched multi-group entry point ──

/// C-compatible group descriptor. Must match C# `MeshGroupHeader`.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct MeshGroupHeader {
    pub vert_start: i32,
    pub vert_count: i32,
    pub colors: *mut u8,
}

/// Process all mesh groups in one P/Invoke call.
///
/// # Safety
/// Pointers must be valid. `groups` array has `num_groups` entries.
/// Each group's `colors` buffer must accommodate `max(vert_index) + 1` Color32 values.
pub unsafe fn apply_all_groups(
    static_wrap_l: *const f32,
    static_wrap_a: *const f32,
    static_wrap_b: *const f32,
    vert_index: *const i32,
    normalized_pos: *const f32,
    config_idx: *const i32,
    config_wrap_starts: *const i32,
    config_smooth_lens: *const i32,
    config_masks: *const i32,
    config_shift_ints: *const i32,
    config_shift_fracs: *const f32,
    config_cycle_comps: *const f32,
    num_configs: usize,
    groups: *const MeshGroupHeader,
    num_groups: usize,
) {
    if num_groups == 0 || num_configs == 0 {
        return;
    }

    // Convert shared data to slices / usize for Sync
    let vert_idx = std::slice::from_raw_parts(vert_index, 0); // placeholder, used per-group
    let norm_pos = std::slice::from_raw_parts(normalized_pos, 0);
    let cfg_idx = std::slice::from_raw_parts(config_idx, 0);
    let _ = (vert_idx, norm_pos, cfg_idx);

    let cfg_wrap_starts = std::slice::from_raw_parts(config_wrap_starts, num_configs);
    let cfg_smooth_lens = std::slice::from_raw_parts(config_smooth_lens, num_configs);
    let cfg_masks = std::slice::from_raw_parts(config_masks, num_configs);
    let cfg_shift_ints = std::slice::from_raw_parts(config_shift_ints, num_configs);
    let cfg_shift_fracs = std::slice::from_raw_parts(config_shift_fracs, num_configs);
    let cfg_cycle_comps = std::slice::from_raw_parts(config_cycle_comps, num_configs);

    let groups_slice = std::slice::from_raw_parts(groups, num_groups);

    // Extract group headers into Sync-safe tuples (raw pointers → usize)
    let group_tasks: Vec<(usize, usize, usize)> = groups_slice
        .iter()
        .map(|g| (g.vert_start as usize, g.vert_count as usize, g.colors as usize))
        .collect();

    // usize casts for Sync (raw pointers are !Sync)
    let wrap_l_addr = static_wrap_l as usize;
    let wrap_a_addr = static_wrap_a as usize;
    let wrap_b_addr = static_wrap_b as usize;
    let vi_addr = vert_index as usize;
    let np_addr = normalized_pos as usize;
    let ci_addr = config_idx as usize;

    // Process groups in parallel; within each group, process vertices sequentially
    group_tasks.par_iter().for_each(|&(start, count, colors_addr)| {
        if count == 0 {
            return;
        }

        let v_idx = std::slice::from_raw_parts((vi_addr as *const i32).add(start), count);
        let n_pos = std::slice::from_raw_parts((np_addr as *const f32).add(start), count);
        let c_idx = std::slice::from_raw_parts((ci_addr as *const i32).add(start), count);

        for i in 0..count {
            let vi = v_idx[i] as usize;
            let np = n_pos[i];
            let cfg = c_idx[i] as usize;

            if cfg >= num_configs {
                let dst = (colors_addr as *mut u8).add(vi * 4);
                *dst = 255;
                *dst.add(1) = 255;
                *dst.add(2) = 255;
                *dst.add(3) = 255;
                continue;
            }

            let wrap_base = cfg_wrap_starts[cfg] as usize;
            let smooth_len = cfg_smooth_lens[cfg] as usize;
            let mask = cfg_masks[cfg] as usize;
            let cycle_comp = cfg_cycle_comps[cfg];

            let np_comp = np * cycle_comp;
            let np_frac = np_comp - (np_comp as i32) as f32;
            let smooth_pos = np_frac * smooth_len as f32;
            let base_idx = smooth_pos as usize;
            let frac = smooth_pos - base_idx as f32;

            let shift_int = cfg_shift_ints[cfg] as usize;
            let shift_frac = cfg_shift_fracs[cfg];

            let mut idx = base_idx.wrapping_sub(shift_int) & mask;
            let mut f = frac - shift_frac;
            if f < 0.0 {
                f += 1.0;
                idx = idx.wrapping_sub(1) & mask;
            }
            let idx_next = (idx + 1) & mask;
            let one_minus_f = 1.0 - f;

            let wrap_l = wrap_l_addr as *const f32;
            let wrap_a = wrap_a_addr as *const f32;
            let wrap_b = wrap_b_addr as *const f32;
            let l_val = *wrap_l.add(wrap_base + idx) * one_minus_f
                + *wrap_l.add(wrap_base + idx_next) * f;
            let a_val = *wrap_a.add(wrap_base + idx) * one_minus_f
                + *wrap_a.add(wrap_base + idx_next) * f;
            let b_val = *wrap_b.add(wrap_base + idx) * one_minus_f
                + *wrap_b.add(wrap_base + idx_next) * f;

            let (r, g, b) = oklab_to_srgb_byte_fast(l_val, a_val, b_val);
            let dst = (colors_addr as *mut u8).add(vi * 4);
            *dst = r;
            *dst.add(1) = g;
            *dst.add(2) = b;
            *dst.add(3) = 255;
        }
    });
}
