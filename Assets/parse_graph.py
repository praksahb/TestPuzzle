import re

logs = """
[EdgeSlotRegistry] Bootstrap complete. 4 slots registered. OPEN: 4, CLOSED: 0
[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(-0.228,1.335) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(0.555,1.787) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(1.072,0.893) len=1.785
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(0.289,0.441) len=1.785
[Registry] Nearby slot: stored=(0.29600,0.45200) len=1.78000 | incoming=(0.28900,0.44000) len=1.78000 | match=True

[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(0.568,-1.809) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(-0.215,-1.357) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(0.302,-0.463) len=1.785
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(1.085,-0.914) len=1.784
[Registry] Nearby slot: stored=(0.29600,-0.45200) len=1.78000 | incoming=(0.30200,-0.46200) len=1.78000 | match=True

[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(2.397,-1.357) len=1.045
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(1.614,-1.808) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(1.098,-0.914) len=1.785
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(1.880,-0.462) len=1.785
[Registry] Nearby slot: stored=(1.08400,-0.91400) len=1.78000 | incoming=(1.09800,-0.91400) len=1.79000 | match=True

[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(-1.524,-0.002) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(-0.742,-0.454) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(-1.258,-1.349) len=1.785
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(-2.041,-0.897) len=1.785
[Registry] Nearby slot: stored=(-0.73800,-0.45200) len=1.05000 | incoming=(-0.74200,-0.45400) len=1.05000 | match=True

[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(-3.327,-0.454) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(-2.544,-0.002) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(-2.028,-0.897) len=1.785
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(-2.810,-1.349) len=1.785
[Registry] Nearby slot: stored=(-2.04000,-0.89600) len=1.79000 | incoming=(-2.02700,-0.89600) len=1.79000 | match=True

[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(-0.742,0.446) len=1.045
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(-1.524,-0.006) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(-2.041,0.889) len=1.785
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(-1.258,1.340) len=1.785
[Registry] Nearby slot: stored=(-0.73800,0.45200) len=1.05000 | incoming=(-0.74200,0.44600) len=1.04000 | match=True
[Registry] Nearby slot: stored=(-1.52400,-0.00200) len=1.05000 | incoming=(-1.52400,-0.00600) len=1.05000 | match=True

[Registry] Kite_1_PerfectCollider(Clone) edge P1→P2: mid=(-0.224,2.236) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P2→P3: mid=(-0.224,1.333) len=1.046
[Registry] Kite_1_PerfectCollider(Clone) edge P3→P4: mid=(-1.257,1.333) len=1.784
[Registry] Kite_1_PerfectCollider(Clone) edge P4→P1: mid=(-1.257,2.236) len=1.785
[Registry] Nearby slot: stored=(-0.22700,1.33500) len=1.05000 | incoming=(-0.22400,1.33200) len=1.05000 | match=True
[Registry] Nearby slot: stored=(-1.25800,1.34000) len=1.79000 | incoming=(-1.25600,1.33200) len=1.78000 | match=True
"""

lines = logs.strip().split('\n')
all_edges = []
all_kites_edges = []
current_kite = []

# Bootstrapped K0 (White kite)
# We can see K1 matches (0.296, 0.452)
# K2 matches (0.296, -0.452)
# ... let's extract the exact edges of K0 from the logs. Since the first log shows the snapped pieces, we know K1 snapped to stored=0.29,0.45, K2 to 0.29,-0.45.
# Wait, let's just make K0 have the exact edges that the later pieces match "stored" onto.
# We know K0 edges roughly from before:
# (-0.215, 1.357, 1.046), (0.568, 1.809, 1.046), (1.085, 0.914, 1.784), (0.302, 0.463, 1.785)
k0 = [
    (-0.215, 1.357, 1.05),
    (0.568, 1.809, 1.05),
    (1.085, 0.914, 1.78),
    (0.302, 0.463, 1.78)
]

# Wait, the K0 from the previous run differs from the K0 in this run? 
# In the log: K2 incoming=(0.302, -0.462) matched stored=(0.296, -0.452). So K0 has an edge at (0.296, -0.452).
# Let's extract all "stored" coords that DON'T match a previously logged "incoming". Those must belong to K0.

incoming_edges = []
stored_edges = []

import re
k_id = 1
for line in lines:
    if "mid=" in line and "Nearby" not in line and "P" in line:
        m = re.search(r'mid=\(([^,]+),([^)]+)\)\s+len=([\d.]+)', line)
        if m:
            x, y, l = map(float, [m.group(1), m.group(2), m.group(3)])
            current_kite.append((x, y, l))
            if len(current_kite) == 4:
                all_kites_edges.append(current_kite)
                for i,e in enumerate(current_kite):
                    all_edges.append((k_id, i, e))
                k_id += 1
                current_kite = []

def dist(e1, e2):
    return ((e1[0]-e2[0])**2 + (e1[1]-e2[1])**2)**0.5

# We know K0 was at the origin. Let's just collect all edges we've ever seen.
all_known_nodes = set(range(1, 8))

adj = {i: [] for i in range(8)}

# Link all edges based on 0.05 dist tolerance.
for i in range(len(all_edges)):
    for j in range(i+1, len(all_edges)):
        k1, idx1, e1 = all_edges[i]
        k2, idx2, e2 = all_edges[j]
        if k1 == k2: continue
        if abs(e1[2] - e2[2]) < 0.05 and dist(e1, e2) < 0.05:
            if k2 not in adj[k1]:
                adj[k1].append(k2)
            if k1 not in adj[k2]:
                adj[k2].append(k1)

# Now, which pieces does K0 connect to?
# K0's edges are the "stored" edges that were matched by K1, K2.. which don't map to any other K1-K7.
for line in lines:
    if "Nearby slot: stored=" in line:
        m_stored = re.search(r'stored=\(([^,]+),([^)]+)\)\s+len=([\d.]+)', line)
        m_inc = re.search(r'incoming=\(([^,]+),([^)]+)\)\s+len=([\d.]+)', line)
        if m_stored and m_inc:
            sx, sy, sl = map(float, [m_stored.group(1), m_stored.group(2), m_stored.group(3)])
            ix, iy, il = map(float, [m_inc.group(1), m_inc.group(2), m_inc.group(3)])
            
            # Find which piece had this 'incoming' edge
            inc_k = -1
            for k, idx, e in all_edges:
                if dist(e, (ix, iy)) < 0.005:
                    inc_k = k
                    break
            
            if inc_k != -1:
                # Did this 'stored' belong to K1-K7?
                stored_k = -1
                for k, idx, e in all_edges:
                    if dist(e, (sx, sy)) < 0.005:
                        stored_k = k
                        break
                
                if stored_k == -1:
                    # Must be K0
                    stored_k = 0
                
                if stored_k not in adj[inc_k]:
                    adj[inc_k].append(stored_k)
                if inc_k not in adj[stored_k]:
                    adj[stored_k].append(inc_k)

degrees = [len(set(adj[i])) for i in range(8)]
print(f"Adjacency: {adj}")
print(f"Degrees: {degrees}")

