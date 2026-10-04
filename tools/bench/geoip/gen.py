import random, ipaddress
rnd = random.Random(20261001)
CC = ["US","CN","DE","GB","JP","FR","NL","AU","CA","RU","BR","IN","KR","IT","ES","SE","PL","CH","SG","HK"] + \
     [a+b for a in "ABCDEFGHIJKLMNOPQRSTUVWXYZ" for b in "ABCDEFGHIJKLMNOPQRSTUVWXYZ"][:229]
WORDS = ["NET","TELECOM","CLOUD","DATA","LINK","FIBRE","GLOBAL","HOST","DIGITAL","COMM","WIRELESS","ONLINE","SYSTEMS","SERVICES","GROUP"]
SUFFIX = ["Inc.","LLC","Ltd","Pty Ltd","GmbH","S.A.","B.V.","AB","Co., Ltd.","Corporation"]
N_ASN = 75000
asn_ids = rnd.sample(range(1, 400000), N_ASN)
owner = {}; country = {}
for a in asn_ids:
    tag = rnd.choice(WORDS) + "-" + rnd.choice(WORDS) + ("-AS" if rnd.random() < .5 else "")
    owner[a] = f"{tag} - {rnd.choice(WORDS).title()} {rnd.choice(WORDS).title()} {rnd.choice(SUFFIX)}"
    country[a] = rnd.choice(CC) if rnd.random() < .7 else rnd.choice(CC[:20])
import itertools
weights = [1.0 / (i + 1) ** 0.8 for i in range(N_ASN)]   # a few big networks, a long tail
cum = list(itertools.accumulate(weights))
_picks = iter(rnd.choices(asn_ids, cum_weights=cum, k=800000))
def pick(): return next(_picks)

def emit_v4(n, path):
    step = (2**32 - 2**24) // n; s = 2**24
    with open(path, "w", newline="\n") as f:
        for i in range(n):
            e = s + rnd.randint(1, step) - 1
            if rnd.random() < 0.08: f.write(f"{s}\t{e}\t0\tNone\tNot routed\n")
            else:
                a = pick(); f.write(f"{s}\t{e}\t{a}\t{country[a]}\t{owner[a]}\n")
            s = e + 1
def emit_v6(n, path):
    base = int(ipaddress.IPv6Address("2001::")); span = 2**125 // n; s = base
    with open(path, "w", newline="\n") as f:
        for i in range(n):
            e = s + rnd.randint(1, span) - 1
            if rnd.random() < 0.08: f.write(f"{ipaddress.IPv6Address(s)}\t{ipaddress.IPv6Address(e)}\t0\tNone\tNot routed\n")
            else:
                a = pick(); f.write(f"{ipaddress.IPv6Address(s)}\t{ipaddress.IPv6Address(e)}\t{a}\t{country[a]}\t{owner[a]}\n")
            s = e + 1
import os; os.makedirs("data", exist_ok=True)   # data/ is not shipped in the package
emit_v4(538417, "data/v4.tsv"); emit_v6(182861, "data/v6.tsv")
with open("data/ips.txt", "w") as f:
    for _ in range(200000): f.write(str(ipaddress.IPv4Address(rnd.randint(2**24, 2**32 - 1))) + "\n")
    for _ in range(50000):  f.write(str(ipaddress.IPv6Address(rnd.randint(base := int(ipaddress.IPv6Address("2001::")), base + 2**125))) + "\n")
print("ok")
