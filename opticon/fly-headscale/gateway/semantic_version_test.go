package main

import "testing"

func TestSemanticVersionPrecedence(t *testing.T) {
	cases := []struct {
		left     string
		right    string
		expected int
	}{
		{"1.0.10", "1.0.9", 1},
		{"1.0.0", "1.0.0-rc.1", 1},
		{"1.0.0-beta.11", "1.0.0-beta.2", 1},
		{"1.0.0-alpha.1", "1.0.0-alpha.beta", -1},
		{"1.0.0+first", "1.0.0+second", 0},
	}
	for _, item := range cases {
		actual, valid := compareSemanticVersions(item.left, item.right)
		if !valid || actual != item.expected {
			t.Fatalf("compare %s to %s: got %d, valid=%v", item.left, item.right, actual, valid)
		}
	}
	for _, invalid := range []string{"1.0", "01.0.0", "1.0.0-01", "1.0.0-"} {
		if _, valid := parseSemanticVersion(invalid); valid {
			t.Fatalf("accepted invalid semantic version %q", invalid)
		}
	}
}
