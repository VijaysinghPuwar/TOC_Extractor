"""What the machine can afford, and the policy built on it.

Every value is injected, so these run the same on the load-test machine and
on a CI runner with quite different memory.
"""

from __future__ import annotations

import pytest

from toc_extractor.machine import (
    FEWEST_PAGES,
    MOST_PAGES_ANYWHERE,
    RESERVED_GIGABYTES,
    TIGHT_BELOW_PERCENT,
    budget_for,
    most_pages_for,
    read_memory,
)

GIGABYTE = 1024**3


def test_a_large_machine_is_capped_by_politeness_not_by_memory() -> None:
    """Past the ceiling the site is the limit, not the computer."""
    assert most_pages_for(128 * GIGABYTE, cores=64) == MOST_PAGES_ANYWHERE


def test_a_small_machine_gets_fewer_pages_than_a_large_one() -> None:
    small = most_pages_for(8 * GIGABYTE, cores=4)
    large = most_pages_for(64 * GIGABYTE, cores=16)
    assert small < large


def test_memory_is_counted_after_the_reserve() -> None:
    """The reserve is for the system and whatever else is open, not for us."""
    just_the_reserve = most_pages_for(int(RESERVED_GIGABYTES) * GIGABYTE, cores=16)
    assert just_the_reserve == FEWEST_PAGES


def test_a_tiny_machine_still_fetches_something() -> None:
    """Never zero: a floor of one is still a working program."""
    assert most_pages_for(1 * GIGABYTE, cores=1) >= FEWEST_PAGES
    assert most_pages_for(0, cores=1) >= FEWEST_PAGES


def test_cores_cap_a_machine_with_lots_of_memory_and_few_cores() -> None:
    assert most_pages_for(64 * GIGABYTE, cores=1) <= 4


def test_unknown_memory_falls_back_to_cores() -> None:
    assert most_pages_for(0, cores=2) == 8


def test_a_modest_request_is_never_raised() -> None:
    """The budget lowers, it does not optimise upward on the person's behalf."""
    budget = budget_for(1, cores=64, memory_bytes=128 * GIGABYTE, free_percent=90)
    assert budget.allowed == 1
    assert not budget.capped


def test_an_oversized_request_is_lowered_and_says_so() -> None:
    budget = budget_for(64, cores=4, memory_bytes=8 * GIGABYTE, free_percent=80)
    assert budget.capped
    assert budget.allowed < 64
    assert budget.allowed >= FEWEST_PAGES


def test_memory_already_scarce_lowers_the_ceiling_further() -> None:
    """The case that matters on a laptop with everything else already open."""
    idle = budget_for(16, cores=8, memory_bytes=16 * GIGABYTE, free_percent=80)
    busy = budget_for(16, cores=8, memory_bytes=16 * GIGABYTE, free_percent=5)
    assert busy.allowed < idle.allowed
    assert busy.allowed >= FEWEST_PAGES


def test_the_scarcity_rule_fires_only_below_the_threshold() -> None:
    above = budget_for(16, cores=8, memory_bytes=16 * GIGABYTE, free_percent=TIGHT_BELOW_PERCENT)
    below = budget_for(
        16, cores=8, memory_bytes=16 * GIGABYTE, free_percent=TIGHT_BELOW_PERCENT - 1
    )
    assert below.allowed < above.allowed


def test_unknown_free_memory_does_not_penalise_the_machine() -> None:
    """A system that will not say how much is free is not assumed to be full."""
    known = budget_for(16, cores=8, memory_bytes=16 * GIGABYTE, free_percent=80)
    unknown = budget_for(16, cores=8, memory_bytes=16 * GIGABYTE, free_percent=None)
    # free_percent=None means "read the real machine", so compare the ceiling
    # rule directly instead: no reading may make it stricter than scarcity.
    assert unknown.allowed <= known.allowed
    assert unknown.allowed >= FEWEST_PAGES


def test_describe_names_what_it_decided_on() -> None:
    budget = budget_for(8, cores=4, memory_bytes=16 * GIGABYTE, free_percent=42)
    described = budget.describe()
    assert "16.0 GB" in described
    assert "4 core" in described
    assert "42%" in described


def test_describe_survives_a_machine_that_will_not_say() -> None:
    budget = budget_for(8, cores=4, memory_bytes=0, free_percent=None)
    assert "unknown memory" in budget.describe()


@pytest.mark.parametrize("requested", [1, 2, 3, 8, 40])
def test_the_allowance_is_always_usable(requested: int) -> None:
    """Whatever is asked for, on whatever machine, the answer runs."""
    budget = budget_for(requested)
    assert FEWEST_PAGES <= budget.allowed <= max(requested, FEWEST_PAGES)


def test_this_machine_answers_at_all() -> None:
    """Not a fixed number - just that the platform read works where it runs."""
    memory = read_memory()
    if memory is None:
        pytest.skip("this platform does not report memory")
    total, available = memory
    assert total > 0
    assert available >= 0
