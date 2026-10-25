"""Turns GeoNames cities15000.txt into a RESP mass-insert file: one SET per city, one INCR per country."""
import sys

src, dst = sys.argv[1], sys.argv[2]


def command(*args):
    parts = [b"*%d\r\n" % len(args)]
    for a in args:
        parts.append(b"$%d\r\n%s\r\n" % (len(a), a))
    return b"".join(parts)


count = 0
with open(src, "rb") as f, open(dst, "wb") as out:
    for line in f:
        row = line.rstrip(b"\n")
        fields = row.split(b"\t")
        geonameid, country = fields[0], fields[8]
        out.write(command(b"SET", b"city:" + geonameid, row))
        out.write(command(b"INCR", b"country:" + country))
        count += 1
print(f"{count} cities -> {dst}")
