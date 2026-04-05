import struct, zlib, re, sys

path = r"E:\dev\VeraPdfSharp\veraPDF-corpus-staging\PDF_A-1a\6.3 Fonts\6.3.8 Unicode character maps\veraPDF test suite 6-3-8-t01-pass-e.pdf"
with open(path, 'rb') as f:
    data = f.read()

text = data.decode('latin-1')
m = re.search(r'29 0 obj\s*<<[^>]*?>>\s*stream\s*', text, re.DOTALL)
stream_start = m.end()
stream_end = text.find('endstream', stream_start)
cff = zlib.decompress(data[stream_start:stream_end])

hdrsize = cff[2]
offset = hdrsize

def read_index(cff, offset):
    count = struct.unpack('>H', cff[offset:offset+2])[0]
    if count == 0:
        return offset + 2, []
    osize = cff[offset+2]
    offsets = []
    for i in range(count+1):
        if osize == 1:
            offsets.append(cff[offset+3+i])
        elif osize == 2:
            offsets.append(struct.unpack('>H', cff[offset+3+i*2:offset+3+i*2+2])[0])
        elif osize == 3:
            b = cff[offset+3+i*3:offset+3+i*3+3]
            offsets.append((b[0]<<16) | (b[1]<<8) | b[2])
        elif osize == 4:
            offsets.append(struct.unpack('>I', cff[offset+3+i*4:offset+3+i*4+4])[0])
    data_start = offset + 3 + (count+1)*osize
    items = []
    for i in range(count):
        items.append(cff[data_start+offsets[i]-1:data_start+offsets[i+1]-1])
    end = data_start + offsets[-1] - 1
    return end, items

# Skip Name INDEX
offset, names = read_index(cff, offset)
print(f"Font names: {[n.decode('ascii') for n in names]}")

# Skip Top DICT INDEX
offset, dicts = read_index(cff, offset)

# Read String INDEX
offset, strings = read_index(cff, offset)
print(f"String INDEX has {len(strings)} entries")

# Standard strings are 0-390; custom are 391+
for i, s in enumerate(strings):
    sid = 391 + i
    print(f"  SID {sid}: \"{s.decode('ascii', errors='replace')}\"")

# SID 395 = custom index 4
if len(strings) > 4:
    print(f"\nGlyph at GID 1 (SID 395): \"{strings[4].decode('ascii', errors='replace')}\"")
print(f"Glyph at GID 2 (SID 1 = 'space' standard)")
