#!/bin/bash

#Update our copy of the source using git
echo "Checking git.."
git pull

#compile first!
echo "Compiling..."
bash compile.sh

echo "Executing..."

#below is for testing
export MONO_THREADS_PER_CPU=200000
mono --debug --gc=sgen program.exe
