N = 33
lines = []
lines.append('TITLE "identity 33"')
lines.append('LUT_3D_SIZE 33')
lines.append('DOMAIN_MIN 0.0 0.0 0.0')
lines.append('DOMAIN_MAX 1.0 1.0 1.0')
for b in range(N):
    for g in range(N):
        for r in range(N):
            lines.append('%.6f %.6f %.6f' % (r/(N-1), g/(N-1), b/(N-1)))
with open('D:/DSH/local-media-player/lut_identity_33.cube', 'w') as f:
    f.write('\n'.join(lines) + '\n')
print('rows=%(d)d' % {'d': N*N*N})
